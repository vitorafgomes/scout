using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Scout.Conversation.Memory;
using Scout.Llm.Abstractions;

namespace Scout.Conversation.Tests;

public sealed class InMemoryConversationMemoryStoreTests
{
    private static readonly DateTimeOffset Origin = new(2026, 5, 30, 12, 0, 0, TimeSpan.Zero);

    private static InMemoryConversationMemoryStore Store(
        ConversationMemoryOptions? options,
        out FakeTimeProvider clock)
    {
        clock = new FakeTimeProvider(Origin);
        return new InMemoryConversationMemoryStore(
            Options.Create(options ?? new ConversationMemoryOptions()),
            clock);
    }

    private static ConversationTurn User(string text) => new(ConversationRole.User, text);

    private static ConversationTurn Assistant(string text) => new(ConversationRole.Assistant, text);

    [Fact]
    public void Load_Returns_Empty_When_Nothing_Stored()
    {
        var store = Store(null, out _);

        store.Load("missing").ShouldBeEmpty();
    }

    [Fact]
    public void Append_Then_Load_Returns_Pair_Oldest_First()
    {
        var store = Store(null, out _);

        store.Append("k", User("q1"), Assistant("a1"));

        var history = store.Load("k");
        history.Count.ShouldBe(2);
        history[0].ShouldBe(User("q1"));
        history[1].ShouldBe(Assistant("a1"));
    }

    [Fact]
    public void Append_Multiple_Pairs_Are_Ordered_Oldest_First()
    {
        var store = Store(null, out _);

        store.Append("k", User("q1"), Assistant("a1"));
        store.Append("k", User("q2"), Assistant("a2"));

        var history = store.Load("k");
        history.Select(t => t.Text).ShouldBe(new[] { "q1", "a1", "q2", "a2" });
    }

    [Fact]
    public void Load_Evicts_Oldest_Pairs_Fifo_Past_MaxTurns()
    {
        var options = new ConversationMemoryOptions { MaxTurns = 2, MaxReplayedChars = int.MaxValue };
        var store = Store(options, out _);

        // MaxTurns + 2 pairs appended; oldest two must be dropped, newest two kept.
        for (var i = 1; i <= options.MaxTurns + 2; i++)
        {
            store.Append("k", User($"q{i}"), Assistant($"a{i}"));
        }

        var history = store.Load("k");
        history.Count.ShouldBe(options.MaxTurns * 2);
        history.Select(t => t.Text).ShouldBe(new[] { "q3", "a3", "q4", "a4" });
    }

    [Fact]
    public void Load_Trims_To_Char_Budget_Keeping_Newest()
    {
        // Each text is 5 chars; budget 10 keeps the newest two entries.
        var options = new ConversationMemoryOptions { MaxTurns = 10, MaxReplayedChars = 10 };
        var store = Store(options, out _);

        store.Append("k", User("aaaaa"), Assistant("bbbbb"));
        store.Append("k", User("ccccc"), Assistant("ddddd"));

        var history = store.Load("k");
        history.Select(t => t.Text).ShouldBe(new[] { "ccccc", "ddddd" });
    }

    [Fact]
    public void Load_Keeps_Newest_Turn_Even_When_It_Exceeds_Budget()
    {
        var options = new ConversationMemoryOptions { MaxTurns = 10, MaxReplayedChars = 3 };
        var store = Store(options, out _);

        store.Append("k", User("aaaaa"), Assistant("bbbbb"));

        var history = store.Load("k");
        history.Count.ShouldBe(1);
        history[0].ShouldBe(Assistant("bbbbb"));
    }

    [Fact]
    public void Load_Returns_Empty_After_Idle_Ttl_Expires()
    {
        var options = new ConversationMemoryOptions { IdleTtl = TimeSpan.FromMinutes(30) };
        var store = Store(options, out var clock);

        store.Append("k", User("q1"), Assistant("a1"));
        clock.Advance(TimeSpan.FromMinutes(31));

        store.Load("k").ShouldBeEmpty();
    }

    [Fact]
    public void Load_Returns_History_Within_Idle_Ttl()
    {
        var options = new ConversationMemoryOptions { IdleTtl = TimeSpan.FromMinutes(30) };
        var store = Store(options, out var clock);

        store.Append("k", User("q1"), Assistant("a1"));
        clock.Advance(TimeSpan.FromMinutes(29));

        store.Load("k").Count.ShouldBe(2);
    }

    [Fact]
    public void Append_After_Idle_Expiry_Starts_Fresh()
    {
        var options = new ConversationMemoryOptions { IdleTtl = TimeSpan.FromMinutes(30) };
        var store = Store(options, out var clock);

        store.Append("k", User("q1"), Assistant("a1"));
        clock.Advance(TimeSpan.FromMinutes(31));
        store.Append("k", User("q2"), Assistant("a2"));

        var history = store.Load("k");
        history.Select(t => t.Text).ShouldBe(new[] { "q2", "a2" });
    }

    [Fact]
    public void Different_Keys_Are_Independent()
    {
        var store = Store(null, out _);

        store.Append("a", User("qa"), Assistant("aa"));
        store.Append("b", User("qb"), Assistant("ab"));

        store.Load("a").Select(t => t.Text).ShouldBe(new[] { "qa", "aa" });
        store.Load("b").Select(t => t.Text).ShouldBe(new[] { "qb", "ab" });
    }

    [Fact]
    public async Task Concurrent_Same_Key_Appends_Are_Atomic_And_Bounded()
    {
        var options = new ConversationMemoryOptions { MaxTurns = 6, MaxReplayedChars = int.MaxValue };
        var store = Store(options, out _);
        const int writers = 64;

        var tasks = Enumerable.Range(0, writers).Select(i => Task.Run(() =>
            store.Append("k", User($"q{i}"), Assistant($"a{i}"))));
        await Task.WhenAll(tasks);

        var history = store.Load("k");

        // No torn list: every entry is well-formed and pairs alternate user/assistant.
        history.Count.ShouldBe(options.MaxTurns * 2);
        for (var i = 0; i < history.Count; i += 2)
        {
            history[i].Role.ShouldBe(ConversationRole.User);
            history[i + 1].Role.ShouldBe(ConversationRole.Assistant);
        }
    }

    [Fact]
    public void Append_Throws_When_Arguments_Null()
    {
        var store = Store(null, out _);

        Should.Throw<ArgumentNullException>(() => store.Append(null!, User("q"), Assistant("a")));
        Should.Throw<ArgumentNullException>(() => store.Append("k", null!, Assistant("a")));
        Should.Throw<ArgumentNullException>(() => store.Append("k", User("q"), null!));
    }

    [Fact]
    public void Load_Throws_When_Key_Null()
    {
        var store = Store(null, out _);

        Should.Throw<ArgumentNullException>(() => store.Load(null!));
    }
}
