using Microsoft.Extensions.Options;
using Scout.Plugin.Common;
using Scout.Plugin.Common.Abstractions;
using Scout.Plugin.Lists;
using Scout.Plugin.Lists.Abstractions;

namespace Scout.Plugin.Lists.Tests;

public sealed class ListsPluginTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "scout-lists-tests", Guid.NewGuid().ToString("N"));

    private JsonFileScoutLocalStore Store() =>
        new(Options.Create(new ScoutLocalStoreOptions { RootDirectory = _dir }));

    private ListsPlugin Plugin() => new(Store());

    [Fact]
    public async Task AddToList_Creates_List_And_Confirms_Count()
    {
        var plugin = Plugin();

        var result = await plugin.AddToListAsync("shopping", "milk", CancellationToken.None);

        result.ShouldBe("Added milk to shopping (1 items).");
    }

    [Fact]
    public async Task AddToList_Appends_And_Reports_Running_Count()
    {
        var plugin = Plugin();

        await plugin.AddToListAsync("shopping", "milk", CancellationToken.None);
        var result = await plugin.AddToListAsync("shopping", "bread", CancellationToken.None);

        result.ShouldBe("Added bread to shopping (2 items).");
    }

    [Fact]
    public async Task AddToList_Trims_Whitespace_From_Inputs()
    {
        var plugin = Plugin();

        var result = await plugin.AddToListAsync("  shopping  ", "  milk  ", CancellationToken.None);

        result.ShouldBe("Added milk to shopping (1 items).");
    }

    [Fact]
    public async Task AddToList_Blank_ListName_Returns_Corrective_Hint()
    {
        var plugin = Plugin();

        var result = await plugin.AddToListAsync("   ", "milk", CancellationToken.None);

        result.ShouldBe("Provide a list name to add to, for example: add milk to shopping.");
    }

    [Fact]
    public async Task AddToList_Blank_Item_Returns_Corrective_Hint()
    {
        var plugin = Plugin();

        var result = await plugin.AddToListAsync("shopping", "   ", CancellationToken.None);

        result.ShouldContain("Provide an item to add to shopping");
    }

    [Fact]
    public async Task ShowList_Named_Returns_Items_One_Per_Line()
    {
        var plugin = Plugin();
        await plugin.AddToListAsync("packing", "socks", CancellationToken.None);
        await plugin.AddToListAsync("packing", "charger", CancellationToken.None);

        var result = await plugin.ShowListAsync("packing");

        result.ShouldBe("socks\ncharger");
    }

    [Fact]
    public async Task ShowList_Missing_Named_List_Returns_Empty_Sentence()
    {
        var plugin = Plugin();

        var result = await plugin.ShowListAsync("packing");

        result.ShouldBe("List packing is empty.");
    }

    [Fact]
    public async Task ShowList_No_Argument_With_No_Lists_Returns_No_Lists_Yet()
    {
        var plugin = Plugin();

        var result = await plugin.ShowListAsync();

        result.ShouldBe("No lists yet.");
    }

    [Fact]
    public async Task ShowList_No_Argument_Lists_Every_List_With_Counts()
    {
        var plugin = Plugin();
        await plugin.AddToListAsync("shopping", "milk", CancellationToken.None);
        await plugin.AddToListAsync("shopping", "bread", CancellationToken.None);
        await plugin.AddToListAsync("packing", "socks", CancellationToken.None);

        var result = await plugin.ShowListAsync();

        // Overview is ordered by list name.
        result.ShouldBe("packing (1 items)\nshopping (2 items)");
    }

    [Fact]
    public async Task Lists_Are_Case_Insensitive_Across_A_Save_Load_Round_Trip()
    {
        // First plugin instance creates "Shopping"; a fresh instance reads the
        // persisted file back and must still treat "shopping" as the same list,
        // proving the case-insensitive comparer survives serialization.
        await Plugin().AddToListAsync("Shopping", "milk", CancellationToken.None);
        await Plugin().AddToListAsync("shopping", "bread", CancellationToken.None);

        var overview = await Plugin().ShowListAsync();

        overview.ShouldBe("Shopping (2 items)");
    }

    [Fact]
    public async Task RemoveFromList_Exact_Match_Removes_And_Confirms()
    {
        var plugin = Plugin();
        await plugin.AddToListAsync("shopping", "milk", CancellationToken.None);
        await plugin.AddToListAsync("shopping", "bread", CancellationToken.None);

        var result = await plugin.RemoveFromListAsync("shopping", "MILK", CancellationToken.None);

        result.ShouldBe("Removed milk from shopping (1 items).");
        (await plugin.ShowListAsync("shopping")).ShouldBe("bread");
    }

    [Fact]
    public async Task RemoveFromList_Substring_Fallback_Removes_Matching_Item()
    {
        var plugin = Plugin();
        await plugin.AddToListAsync("shopping", "semi-skimmed milk", CancellationToken.None);

        var result = await plugin.RemoveFromListAsync("shopping", "milk", CancellationToken.None);

        result.ShouldBe("Removed semi-skimmed milk from shopping (0 items).");
    }

    [Fact]
    public async Task RemoveFromList_No_Match_Returns_Closest_Items_Hint()
    {
        var plugin = Plugin();
        await plugin.AddToListAsync("shopping", "bread", CancellationToken.None);
        await plugin.AddToListAsync("shopping", "butter", CancellationToken.None);

        var result = await plugin.RemoveFromListAsync("shopping", "bttter", CancellationToken.None);

        result.ShouldContain("No item matching \"bttter\" in shopping.");
        result.ShouldContain("butter");
    }

    [Fact]
    public async Task RemoveFromList_Missing_List_Returns_Empty_Sentence()
    {
        var plugin = Plugin();

        var result = await plugin.RemoveFromListAsync("shopping", "milk", CancellationToken.None);

        result.ShouldBe("List shopping is empty.");
    }

    [Fact]
    public async Task RemoveFromList_Blank_Item_Returns_Corrective_Hint()
    {
        var plugin = Plugin();

        var result = await plugin.RemoveFromListAsync("shopping", "  ", CancellationToken.None);

        result.ShouldContain("Provide the item to remove from shopping");
    }

    [Fact]
    public async Task ClearList_Reports_Removed_Count_And_Empties_List()
    {
        var plugin = Plugin();
        await plugin.AddToListAsync("shopping", "milk", CancellationToken.None);
        await plugin.AddToListAsync("shopping", "bread", CancellationToken.None);

        var result = await plugin.ClearListAsync("shopping", CancellationToken.None);

        result.ShouldBe("Cleared shopping (2 items removed).");
        (await plugin.ShowListAsync("shopping")).ShouldBe("List shopping is empty.");
    }

    [Fact]
    public async Task ClearList_Missing_List_Reports_Zero_Removed()
    {
        var plugin = Plugin();

        var result = await plugin.ClearListAsync("shopping", CancellationToken.None);

        result.ShouldBe("Cleared shopping (0 items removed).");
    }

    [Fact]
    public async Task ClearList_Blank_ListName_Returns_Corrective_Hint()
    {
        var plugin = Plugin();

        var result = await plugin.ClearListAsync("   ", CancellationToken.None);

        result.ShouldBe("Provide the list name to clear, for example: clear shopping.");
    }

    [Fact]
    public async Task AddToList_Storage_Failure_Returns_Clean_Error_Without_Throwing()
    {
        var store = Substitute.For<IScoutLocalStore>();
        store.MutateAsync<ListsDocument>(
                Arg.Any<string>(), Arg.Any<Action<ListsDocument>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<ListsDocument>(new IOException("disk full")));
        var plugin = new ListsPlugin(store);

        var result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.AddToListAsync("shopping", "milk", CancellationToken.None));

        result.ShouldBe("Could not access local lists storage. Try again.");
    }

    [Fact]
    public async Task ShowList_Timeout_Returns_Clean_Error_Without_Throwing()
    {
        var store = Substitute.For<IScoutLocalStore>();
        store.ReadOrCreateAsync<ListsDocument>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<ListsDocument>(new OperationCanceledException()));
        var plugin = new ListsPlugin(store);

        var result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.ShowListAsync("shopping"));

        result.ShouldBe("The lists operation timed out. Try again.");
    }

    [Fact]
    public async Task RemoveFromList_Storage_Failure_Returns_Clean_Error_Without_Throwing()
    {
        var store = Substitute.For<IScoutLocalStore>();
        store.MutateAsync<ListsDocument>(
                Arg.Any<string>(), Arg.Any<Action<ListsDocument>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<ListsDocument>(new IOException("disk full")));
        var plugin = new ListsPlugin(store);

        var result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.RemoveFromListAsync("shopping", "milk", CancellationToken.None));

        result.ShouldBe("Could not access local lists storage. Try again.");
    }

    [Fact]
    public void Ctor_Throws_When_Store_Null()
    {
        Should.Throw<ArgumentNullException>(() => new ListsPlugin(null!));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
