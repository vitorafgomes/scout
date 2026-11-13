using System.Globalization;

namespace Scout.Audit.Tests;

public sealed class AuditPlannerTests
{
    [Fact]
    public void The_plan_reads_the_manifests_configuration_and_top_level_files_first_each_folder_next_and_the_tests_last()
    {
        using var tree = new TempFolder();
        foreach (var file in new[]
        {
            "README.md", "Shop.slnx", ".editorconfig", ".github/workflows/ci.yml", "src/Orders/Orders.csproj", "src/Orders/Checkout.cs",
            "src/Billing/Invoice.cs", "tests/Orders.Tests/CheckoutTests.cs", "ui/app.spec.ts", "ui/app.ts", "src/Orders/bin/Orders.dll", ".env",
        })
        {
            tree.File(file);
        }

        var plan = AuditPlanner.Plan(tree.Path, 40, 200_000);

        plan.Select(b => (b.Area, string.Join(",", b.Files))).ShouldBe(
        [
            (AuditPlanner.ProjectFilesArea, ".github/workflows/ci.yml,README.md,Shop.slnx,src/Orders/Orders.csproj"),
            ("src, ui", "src/Billing/Invoice.cs,src/Orders/Checkout.cs,ui/app.ts"),
            ("tests, ui", "tests/Orders.Tests/CheckoutTests.cs,ui/app.spec.ts"),
        ]);
    }

    [Fact]
    public void A_folder_past_40_files_is_split()
    {
        using var tree = new TempFolder();
        for (var i = 0; i < 45; i++)
        {
            tree.File(string.Create(CultureInfo.InvariantCulture, $"src/Big/File{i:D2}.cs"));
        }

        var plan = AuditPlanner.Plan(tree.Path, 40, 200_000);

        plan.Select(b => (b.Area, b.Files.Count)).ShouldBe([("src/Big (1/2)", 40), ("src/Big (2/2)", 5)]);
    }

    [Fact]
    public void With_the_default_caps_a_folder_past_60000_characters_is_split_by_its_subfolders()
    {
        using var tree = new TempFolder();
        tree.File("src/Orders/A.cs", 25_000);
        tree.File("src/Orders/B.cs", 25_000);
        tree.File("src/Billing/C.cs", 15_000);
        var defaults = new AuditOptions();

        var plan = AuditPlanner.Plan(tree.Path, defaults.MaxBatchFiles, defaults.MaxBatchCharacters);

        plan.Select(b => (b.Area, string.Join(",", b.Files))).ShouldBe(
        [
            ("src/Billing", "src/Billing/C.cs"),
            ("src/Orders", "src/Orders/A.cs,src/Orders/B.cs"),
        ]);
    }

    [Fact]
    public void A_file_longer_than_a_read_returns_is_marked_as_partly_readable()
    {
        using var tree = new TempFolder();
        tree.File("src/Orders/Small.cs", 20_000);
        tree.File("src/Orders/Big.cs", 20_001);

        var plan = AuditPlanner.Plan(tree.Path, 40, 60_000);

        plan.ShouldHaveSingleItem().Partial.ShouldBe(["src/Orders/Big.cs"]);
    }

    [Fact]
    public void An_audit_plan_covers_every_file_the_index_reads_exactly_once_in_the_same_order_every_time()
    {
        using var tree = new TempFolder();
        for (var i = 0; i < 30; i++)
        {
            tree.File($"src/M{i % 7}/Sub{i % 3}/F{i}.cs", 20_000);
            tree.File($"tests/M{i % 4}/F{i}Tests.cs", 15_000);
        }

        tree.File("node_modules/lib/index.js");
        var first = AuditPlanner.Plan(tree.Path, 5, 60_000);
        var second = AuditPlanner.Plan(tree.Path, 5, 60_000);

        var files = first.SelectMany(b => b.Files).ToList();
        files.Count.ShouldBe(60);
        files.Distinct().Count().ShouldBe(60);
        files.ShouldNotContain("node_modules/lib/index.js");
        first.ShouldAllBe(b => b.Files.Count <= 5 && b.Characters <= 60_000);
        second.Select(b => b.Area + string.Join(",", b.Files)).ShouldBe(first.Select(b => b.Area + string.Join(",", b.Files)));
    }

    [Fact]
    public void A_plan_over_a_given_list_splits_and_orders_as_the_folder_plan_and_filters_nothing()
    {
        AuditPlanner.Entry[] given =
        [
            new("tests/CheckoutTests.cs", 10),
            new("src/Orders/Gateway.cs", 30_000),
            new("src/Orders/bin/Orders.dll", 10),
            new("node_modules/lib/index.js", 10),
            new("README.md", 100),
            new("src/Orders/Checkout.cs", 25_000),
            new(".env", 10),
        ];

        var plan = AuditPlanner.Plan(given, 2, 50_000);

        plan.Select(b => (b.Area, string.Join(",", b.Files), string.Join(",", b.Partial ?? []))).ShouldBe(
        [
            (AuditPlanner.ProjectFilesArea, ".env,README.md", ""),
            ("node_modules", "node_modules/lib/index.js", ""),
            ("src/Orders/bin, src/Orders (1/2)", "src/Orders/bin/Orders.dll,src/Orders/Checkout.cs", "src/Orders/Checkout.cs"),
            ("src/Orders (2/2)", "src/Orders/Gateway.cs", "src/Orders/Gateway.cs"),
            ("tests", "tests/CheckoutTests.cs", ""),
        ]);
    }

    [Fact]
    public void The_folder_plan_and_the_list_plan_of_the_same_files_are_the_same_batches()
    {
        AuditPlanner.Entry[] indexable =
        [
            new("README.md", 300), new("Shop.slnx", 200), new(".github/workflows/ci.yml", 400), new("src/Orders/Orders.csproj", 500),
            new("src/Orders/Checkout.cs", 21_000), new("src/Orders/Gateway.cs", 9_000), new("src/Billing/Invoice.cs", 30_000),
            new("src/Billing/Tax/Rates.cs", 12_000), new("ui/app.ts", 4_000), new("ui/app.spec.ts", 2_000), new("tests/Orders.Tests/CheckoutTests.cs", 8_000),
        ];
        using var tree = new TempFolder();
        foreach (var entry in indexable)
        {
            tree.File(entry.Path, (int)entry.Size);
        }

        tree.File("node_modules/lib/index.js");
        tree.File("src/Orders/bin/Orders.dll");
        tree.File(".env");

        var fromFolder = AuditPlanner.Plan(tree.Path, 3, 40_000);
        var fromList = AuditPlanner.Plan([.. indexable.Reverse()], 3, 40_000);

        fromFolder.Count.ShouldBeGreaterThan(3);
        fromList.Select(Shape).ShouldBe(fromFolder.Select(Shape));
    }

    private static string Shape(AuditBatchPlan batch) =>
        string.Create(CultureInfo.InvariantCulture, $"{batch.Area}|{string.Join(",", batch.Files)}|{batch.Characters}|{string.Join(",", batch.Partial ?? [])}");
}
