using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Scout.Skills.Tests;

/// <summary>
/// Without embeddings the turn's skill is picked by the words of the message against each bundled skill's name and
/// description, in Portuguese and English: a match picks the skill that declares the functions the request needs, and
/// a message that matches no such skill picks none, so the turn keeps the general set.
/// </summary>
public sealed class LexicalSkillIndexTests
{
    private static readonly LexicalSkillIndex Index = new(
        new SkillFileLoader(NullLogger<SkillFileLoader>.Instance).Load(Path.Combine(AppContext.BaseDirectory, "skills")));

    [Theory]
    [InlineData("review PR 42 on acme/shop", "code-review")]
    [InlineData("revisa esse PR", "code-review")]
    [InlineData("faz o code review do PR 12", "code-review")]
    [InlineData("audit acme/shop", "code-audit")]
    [InlineData("audit this repository", "code-audit")]
    [InlineData("analisa o código do projeto", "code-audit")]
    [InlineData("procura bugs no repositório", "code-audit")]
    [InlineData("stop the audit of acme/shop", "code-audit")]
    [InlineData("para a auditoria", "code-audit")]
    [InlineData("how is the audit going", "code-audit")]
    [InlineData("Could you analyze the Acme project and see if there's anything wrong with what was implemented?", "code-audit")]
    [InlineData("audita o repositório acme/shop", "code-audit")]
    [InlineData("faz a review do repositório todo", "code-audit")]
    [InlineData("code review do projeto inteiro", "code-audit")]
    [InlineData("what could we improve in this repo", "project-improvements")]
    [InlineData("o que dá para melhorar no projeto", "project-improvements")]
    [InlineData("open an issue about the login bug", "github-issues")]
    [InlineData("abre uma issue sobre o erro de login", "github-issues")]
    [InlineData("comment on issue 12", "github-issues")]
    [InlineData("make a video about black holes", "youtube-operator")]
    [InlineData("faz um vídeo sobre buracos negros", "youtube-operator")]
    [InlineData("how is my video going", "youtube-operator")]
    [InlineData("edit the text of scene 3", "youtube-operator")]
    [InlineData("refaz a narração da cena 2", "youtube-operator")]
    public void A_Message_Picks_The_Skill_Its_Words_Describe(string message, string skill)
    {
        Index.Select(message, topN: 1).Select(s => s.Name).ShouldBe([skill]);
    }

    public static TheoryData<string> ReviewsOfANamedPullRequest()
    {
        string[] portuguese =
        [
            "faz o code review do PR 5 e procura {0}",
            "revisa o PR #5 e procura {0}",
            "faz o review do pull request 5 e veja se tem {0}",
            "revisa o PR 5 do acme/shop olhando {0}",
            "revisa os PRs abertos do acme/shop e procura {0}",
            "revisa https://github.com/acme/shop/pull/5 procurando {0}",
            "revisa acme/shop#5 procurando {0}",
        ];
        string[] english =
        [
            "code review of PR 5 looking for {0}",
            "review PR #5 and check {0}",
            "code review of pull request 5 looking for {0}",
            "review the open PRs of acme/shop for {0}",
            "review https://github.com/acme/shop/pull/5 for {0}",
            "review acme/shop#5 for {0}",
        ];
        string[] portugueseFocus = ["bugs", "problemas de segurança", "performance", "padrões não seguidos", "testes faltando"];
        string[] englishFocus = ["bugs", "security problems", "performance issues", "patterns not followed", "missing tests"];

        var data = new TheoryData<string>
        {
            "faz o code review do PR 5 e procura bugs",
            "faz o review do PR 5 e veja se tem problemas de segurança",
            "code review of pull request 5 looking for bugs and security problems",
            "analisa o PR 5 do acme/shop",
            "faz o code review do PR 5 do acme/shop",
            "review https://github.com/acme/shop/pull/5",
            "Could you do a code review of PR 5 on acme/shop?",
        };
        foreach (var (templates, focuses) in new[] { (portuguese, portugueseFocus), (english, englishFocus) })
        {
            foreach (var template in templates)
            {
                foreach (var focus in focuses)
                {
                    data.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, template, focus));
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ReviewsOfANamedPullRequest))]
    public void A_Request_For_A_Code_Review_Of_A_Pull_Request_Is_A_Review_Whatever_Else_It_Asks_To_Look_For(string message)
    {
        Index.Select(message, topN: 1).Select(s => s.Name).ShouldBe(["code-review"]);
    }

    [Theory]
    [InlineData("code review", "code-review")]
    [InlineData("faz o code review do acme/shop", "code-review")]
    [InlineData("audita o repositório acme/shop, o PR 5 mexeu em tudo", "code-audit")]
    [InlineData("review the whole repository, PR 5 touched everything", "code-audit")]
    [InlineData("faz a review do projeto inteiro, não só do PR 5", "code-audit")]
    [InlineData("review PR 5, it touches the whole project", "code-review")]
    [InlineData("revisa o PR 5, ele mexe no projeto inteiro", "code-review")]
    public void A_Review_With_No_Pull_Request_Stays_A_Review_And_Explicit_Whole_Repository_Words_Win_Over_A_Named_Pull_Request(string message, string skill)
    {
        Index.Select(message, topN: 1).Select(s => s.Name).ShouldBe([skill]);
    }

    [Theory]
    [InlineData("o PR 5 tem problemas de segurança?")]
    [InlineData("PR 5: procura bugs e problemas de performance")]
    [InlineData("acme/shop#5 has security problems")]
    [InlineData("os PRs abertos seguem os padrões do projeto?")]
    public void A_Message_That_Names_A_Pull_Request_Without_Asking_For_A_Review_Never_Picks_The_Audit(string message)
    {
        Index.Select(message, topN: 1).Select(s => s.Name).ShouldNotContain("code-audit");
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("oi, tudo bem?")]
    [InlineData("o que é isso?")]
    [InlineData("what is the weather in London")]
    [InlineData("what is this repository about")]
    [InlineData("list open pull requests of acme/shop")]
    [InlineData("post it")]
    [InlineData("what time is it")]
    [InlineData("how do I write async code in python")]
    [InlineData("quais pods estão falhando")]
    [InlineData("")]
    public void A_Message_That_Describes_No_Skill_With_Functions_Picks_None(string message)
    {
        Index.Select(message, topN: 1).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("faz uma ANALISE")]
    [InlineData("faz uma análise")]
    public void Words_Fold_Case_And_Accents_So_Analise_Matches_Análise(string message)
    {
        var index = new LexicalSkillIndex(
        [
            new Skill("datasets", "Análise de dados - \"análise dos dados\".", "d", "/d.md") { Functions = ["Data.analyse"] },
        ]);

        index.Select(message, topN: 1).Select(s => s.Name).ShouldBe(["datasets"]);
    }

    [Fact]
    public void Words_After_The_Not_For_Clause_Of_A_Description_Do_Not_Pick_That_Skill()
    {
        var index = new LexicalSkillIndex(
        [
            new Skill("alpha", "Planning a release - \"plan the release\". Not for deploying the container image, not for a container image deploy, not for image deploys.", "a", "/a.md") { Functions = ["A.plan"] },
            new Skill("beta", "Shipping a container image - \"deploy the image\".", "b", "/b.md") { Functions = ["B.deploy"] },
        ]);

        index.Select("deploy the container image", topN: 1).Select(s => s.Name).ShouldBe(["beta"]);
    }

    [Fact]
    public void A_Top_Skill_That_Declares_No_Function_Is_Left_To_The_Model()
    {
        var index = new LexicalSkillIndex(
        [
            new Skill("python", "Python typing and packaging - \"python types\".", "p", "/p.md"),
            new Skill("review", "Reviewing a pull request - \"review the pull request\".", "r", "/r.md") { Functions = ["GitHubActions.submit_pull_request_review"] },
        ]);

        index.Select("python types", topN: 1).ShouldBeEmpty();
        index.Select("review the pull request", topN: 1).Select(s => s.Name).ShouldBe(["review"]);
    }
}
