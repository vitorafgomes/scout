using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Scout.Conversation.Memory;
using Scout.Llm.Abstractions;

namespace Scout.Conversation.Tests;

public sealed class InMemoryConversationMemoryStoreTests
{
    private static readonly string[] ExpectedQ1A1Q2A2 = ["q1", "a1", "q2", "a2"];
    private static readonly string[] ExpectedQ3A3Q4A4 = ["q3", "a3", "q4", "a4"];
    private static readonly string[] ExpectedCccccddddd = ["ccccc", "ddddd"];
    private static readonly string[] ExpectedQ2A2 = ["q2", "a2"];
    private static readonly string[] ExpectedQaaa = ["qa", "aa"];
    private static readonly string[] ExpectedQbab = ["qb", "ab"];
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
    public void An_Append_For_A_Turn_Already_Cancelled_Stores_Nothing_And_An_Earlier_One_Is_Gone_After_The_Clear()
    {
        var store = Store(null, out _);
        using var running = new CancellationTokenSource();
        store.Append("local", User("q1"), Assistant("a1"), running.Token);

        running.Cancel();
        store.Clear("local");
        store.Append("local", User("q2"), Assistant("a2"), running.Token);

        store.Load("local").ShouldBeEmpty();
    }

    [Fact]
    public void An_Append_Sweeps_Conversations_Idle_Past_The_Ttl_So_One_Shot_Threads_Do_Not_Pile_Up()
    {
        var store = Store(new ConversationMemoryOptions { IdleTtl = TimeSpan.FromMinutes(30) }, out var clock);
        for (var i = 0; i < 50; i++)
        {
            store.Append($"thread-{i}", User("q"), Assistant("a"));
        }
        store.Count.ShouldBe(50);

        clock.Advance(TimeSpan.FromMinutes(31));
        store.Append("thread-new", User("q"), Assistant("a"));

        store.Count.ShouldBe(1);
        store.Load("thread-new").Count.ShouldBe(2);
    }

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
        history.Select(t => t.Text).ShouldBe(ExpectedQ1A1Q2A2);
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
        history.Select(t => t.Text).ShouldBe(ExpectedQ3A3Q4A4);
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
        history.Select(t => t.Text).ShouldBe(ExpectedCccccddddd);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    public void Load_Keeps_The_Newest_Pair_Whole_Even_When_It_Exceeds_Budget(int budget)
    {
        var options = new ConversationMemoryOptions { MaxTurns = 10, MaxReplayedChars = budget };
        var store = Store(options, out _);

        store.Append("k", User("aaaaa"), Assistant("bb"));

        store.Load("k").ShouldBe([User("aaaaa"), Assistant("bb")]);
    }

    [Fact]
    public void The_Character_Budget_Trims_Whole_Pairs_So_No_Answer_Is_Replayed_Without_Its_Question()
    {
        var options = new ConversationMemoryOptions { MaxTurns = 10, MaxReplayedChars = 12 };
        var store = Store(options, out _);

        store.Append("k", User("aaaaa"), Assistant("bb"));
        store.Append("k", User("ccc"), Assistant("ddddd"));

        store.Load("k").ShouldBe([User("ccc"), Assistant("ddddd")]);
    }

    [Fact]
    public void An_Append_Racing_The_Removal_Of_Its_Expired_Conversation_Keeps_The_Turn()
    {
        var clock = new GatedClock(Origin);
        var store = new InMemoryConversationMemoryStore(
            Options.Create(new ConversationMemoryOptions { IdleTtl = TimeSpan.FromMinutes(30) }), clock);
        store.Append("k", User("q1"), Assistant("a1"));
        clock.Now = Origin.AddMinutes(31);

        var loader = new Thread(() =>
        {
            clock.Gated.Value = true;
            store.Load("k");
        });
        loader.Start();
        clock.Entered.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue("the load must be holding the expired conversation's lock");
        var appender = new Thread(() => store.Append("k", User("q2"), Assistant("a2")));
        appender.Start();
        SpinWait.SpinUntil(() => (appender.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5))
            .ShouldBeTrue("the append must be waiting on the expired conversation's lock");
        clock.Release.Set();
        loader.Join();
        appender.Join();

        store.Load("k").ShouldBe([User("q2"), Assistant("a2")]);
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
        history.Select(t => t.Text).ShouldBe(ExpectedQ2A2);
    }

    [Fact]
    public void Different_Keys_Are_Independent()
    {
        var store = Store(null, out _);

        store.Append("a", User("qa"), Assistant("aa"));
        store.Append("b", User("qb"), Assistant("ab"));

        store.Load("a").Select(t => t.Text).ShouldBe(ExpectedQaaa);
        store.Load("b").Select(t => t.Text).ShouldBe(ExpectedQbab);
    }

    [Fact]
    public void Clear_Forgets_Every_Turn_Of_That_Conversation_And_Leaves_The_Others()
    {
        var store = Store(null, out _);
        store.Append("a", User("qa"), Assistant("aa"));
        store.Append("b", User("qb"), Assistant("ab"));

        store.Clear("a");
        store.Clear("never-stored");

        store.Load("a").ShouldBeEmpty();
        store.Load("b").Select(t => t.Text).ShouldBe(ExpectedQbab);
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

    [Fact]
    public void A_Load_Dropping_An_Expired_Conversation_Never_Removes_The_Newer_One_Under_The_Same_Key()
    {
        var clock = new GatedClock(Origin);
        var store = new InMemoryConversationMemoryStore(
            Options.Create(new ConversationMemoryOptions { IdleTtl = TimeSpan.FromMinutes(30) }), clock);
        store.Append("k", User("q1"), Assistant("a1"));
        clock.Now = Origin.AddMinutes(31);

        var loader = new Thread(() =>
        {
            clock.Gated.Value = true;
            store.Load("k");
        });
        loader.Start();
        clock.Entered.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue("the load must be holding the expired conversation's lock");
        store.Clear("k");
        store.Append("k", User("q2"), Assistant("a2"));
        clock.Release.Set();
        loader.Join();

        store.Load("k").ShouldBe([User("q2"), Assistant("a2")]);
    }

    private sealed class GatedClock(DateTimeOffset now) : TimeProvider
    {
        private int _armed = 1;

        public DateTimeOffset Now { get; set; } = now;

        public AsyncLocal<bool> Gated { get; } = new();

        public ManualResetEventSlim Entered { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public override DateTimeOffset GetUtcNow()
        {
            if (Gated.Value && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Entered.Set();
                Release.Wait();
            }

            return Now;
        }
    }
}
