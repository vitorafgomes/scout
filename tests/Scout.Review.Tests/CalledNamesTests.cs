namespace Scout.Review.Tests;

/// <summary>The names a finding's quote calls, found by code so their definitions can be read.</summary>
public sealed class CalledNamesTests
{
    [Theory]
    [InlineData("C#", "var user = _store.Find(id);\nif (user is null) return Refuse(request, \"No user\");\nthrow new NotFoundException(nameof(id));", new[] { "Find", "Refuse", "NotFoundException" })]
    [InlineData("TypeScript", "const page = await this.client.get(url);\nif(page?.items.length) { render(page?.first()); }", new[] { "get", "render", "first" })]
    [InlineData("Python", "def handle(self, request):\n    for item in self.items(request):\n        print(item)", new[] { "handle", "items", "print" })]
    [InlineData("Go", "if err := s.repo.Save(ctx, order); err != nil {\n\treturn fmt.Errorf(\"save: %w\", err)\n}", new[] { "Save", "Errorf" })]
    [InlineData("Java", "for(Item i : items) { total = total.add(i.price()); }\nswitch(kind) { default: log::warn(kind); }", new[] { "add", "price", "warn" })]
    [InlineData("C++", "auto n = node->next(); while(n) { n = n->next(); }", new[] { "next" })]
    public void D1_The_names_called_in_a_quote_are_found_in_order_once_each_and_keywords_are_not_names(string language, string quote, string[] names) =>
        CalledNames.In(quote).ShouldBe(names, $"the names a {language} quote calls, each once, in the order they appear, without keywords");

    [Fact]
    public void Control_and_declaration_keywords_followed_by_a_parenthesis_are_never_names()
    {
        CalledNames.In("if(a) foreach(b) while(c) switch(d) catch(e) using(f) lock(g) return(h) sizeof(i) typeof(j) nameof(k) function(l) def(m) fn(n) func(o) for(p)")
            .ShouldBeEmpty("a keyword before a parenthesis is not a call");
    }

    [Fact]
    public void At_most_eight_names_are_given()
    {
        CalledNames.In(string.Join(" ", Enumerable.Range(1, 12).Select(n => $"call{n}();")))
            .ShouldBe([.. Enumerable.Range(1, 8).Select(n => $"call{n}")], "the first eight names, in order");
        CalledNames.MaxNames.ShouldBe(8, "a quote gives eight names at most");
    }

    [Fact]
    public void A_quote_with_no_call_gives_no_name() =>
        CalledNames.In("var total = 0m;").ShouldBeEmpty("a quote that calls nothing names nothing");
}
