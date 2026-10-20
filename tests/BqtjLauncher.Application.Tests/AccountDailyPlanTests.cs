using System.IO;
using BqtjLauncher.Infrastructure;

namespace BqtjLauncher.Application.Tests;

/// <summary>只使用虚构账号验证默认日常初始化、首次选档回写和模板迁移，不访问生产账号库。</summary>
public sealed class AccountDailyPlanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bqtj-daily-" + Guid.NewGuid().ToString("N"));
    private static ScriptAccount Account(string name) => new(Guid.NewGuid(), name, "");
    public AccountDailyPlanTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void SameNamedAccountsHaveSeparateDefaultsAndDeletingOneDoesNotRecreateIt()
    {
        using var files = new ScriptFileStore(_root);
        var first = Account("同名"); var second = Account("同名");
        Assert.True(files.EnsureAccountDailyPlans([first, second]));
        var plans = files.ListPlans(out _);
        Assert.Equal(2, plans.Count);
        var a = plans.Single(plan => plan.DefaultAccountId == first.Id);
        var b = plans.Single(plan => plan.DefaultAccountId == second.Id);
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(first.Id, a.AccountBindings["本账号"]);
        Assert.Equal(second.Id, b.AccountBindings["本账号"]);
        files.DeletePlan(a.Id);
        Assert.False(files.EnsureAccountDailyPlans([first with { Name = "改名" }, second]));
        Assert.Equal(b.Id, Assert.Single(files.ListPlans(out _)).Id);
    }

    [Fact]
    public void AddingAnotherAccountKeepsExistingEditedSource()
    {
        using var files = new ScriptFileStore(_root);
        var first = Account("甲"); var second = Account("乙");
        files.EnsureAccountDailyPlans([first]);
        var edited = Assert.Single(files.ListPlans(out _));
        edited.Source = "日常操作,本账号,存档6\n关闭窗口,本账号";
        files.SavePlan(edited);
        files.EnsureAccountDailyPlans([first, second]);
        Assert.Equal(edited.Source, files.ListPlans(out _).Single(plan => plan.Id == edited.Id).Source);
        Assert.Single(files.ListPlans(out _), plan => plan.DefaultAccountId == second.Id);
    }

    [Fact]
    public void FirstSelectionIsWrittenToSourceAndStrictCompilationNoLongerNeedsAPrompt()
    {
        var account = Account("甲"); var plan = AccountDailyPlans.Create(account);
        Assert.Throws<InvalidDataException>(() => new ScriptProgramCompiler([], [account], []).Compile(plan));
        var preview = new ScriptProgramCompiler([], [account], []).Compile(plan, allowSaveSelection: true);
        var request = Assert.Single(AccountDailyPlans.RequiredSelections(preview));
        Assert.Equal(plan.Id, request.PlanId); Assert.Equal(account.Id, request.Account.Id);
        var configured = AccountDailyPlans.ApplySelection(plan, request, 5);
        Assert.Contains("日常操作,本账号,存档5", configured.Source);
        Assert.All(plan.Source.Split('\n').Where(line => line.TrimStart().StartsWith('#')), comment => Assert.Contains(comment, configured.Source));
        Assert.Equal(5, Assert.Single(new ScriptProgramCompiler([], [account], []).Compile(configured).Commands).SaveNumber);
        Assert.Empty(AccountDailyPlans.RequiredSelections(new ScriptProgramCompiler([], [account], []).Compile(configured)));
        Assert.Contains(AccountDailyPlans.PendingSave, plan.Source);
    }

    [Fact]
    public void RepeatedChildCallsAskOnceAndKeepTheChildIdentity()
    {
        var account = Account("甲"); var child = AccountDailyPlans.Create(account);
        var root = new ScriptPlan { Version = 2, Source = "调用,子\n调用,子" };
        root.PlanBindings["子"] = child.Id;
        var preview = new ScriptProgramCompiler([child], [account], []).Compile(root, allowSaveSelection: true);
        var request = Assert.Single(AccountDailyPlans.RequiredSelections(preview));
        Assert.Single(request.Lines);
        var configured = AccountDailyPlans.ApplySelection(child, request, 8);
        var ready = new ScriptProgramCompiler([configured], [account], []).Compile(root);
        Assert.Equal(2, ready.Commands.Count);
        Assert.All(ready.Commands, command => Assert.Equal(8, command.SaveNumber));
    }

    [Fact]
    public void SelectionOnlyChangesTheArgumentAndKeepsConstantDefinitions()
    {
        var account = Account("甲"); var plan = AccountDailyPlans.Create(account);
        plan.Source = "日常操作,本账号,$默认档";
        plan.Constants["默认档"] = AccountDailyPlans.PendingSave;
        var request = Assert.Single(AccountDailyPlans.RequiredSelections(
            new ScriptProgramCompiler([], [account], []).Compile(plan, allowSaveSelection: true)));
        var ready = AccountDailyPlans.ApplySelection(plan, request, 2);
        Assert.Equal("日常操作,本账号,存档2", ready.Source);
        Assert.Equal(AccountDailyPlans.PendingSave, ready.Constants["默认档"]);
        Assert.Equal(account.Id, ready.DefaultAccountId);
    }

    [Fact]
    public void TemplateMigrationRemovesReadNamesAddsGlobalExampleAndPreservesEditedBody()
    {
        using var files = new ScriptFileStore(_root);
        var daily = BuiltInScriptPlans.Create()[0];
        daily.Source = "方案设置,子方案,是\n方案设置,面板可见,否\n日常操作,账号名称,存档7";
        files.SavePlan(daily);
        var retired = new ScriptPlan
        {
            Id = BuiltInScriptPlans.RetiredReadNamesId,
            Version = 2,
            Name = "读取名称并保留窗口",
            IsBuiltIn = true,
            Source = "读取存档名称,示例"
        };
        files.SavePlan(retired);
        File.WriteAllText(Path.Combine(_root, "plans", ".built-ins-v1"), "1");
        files.EnsureBuiltInPlans();
        var plans = files.ListPlans(out _);
        Assert.DoesNotContain(plans, plan => plan.Id == retired.Id);
        Assert.Contains("存档7", plans.Single(plan => plan.Id == daily.Id).Source);
        Assert.Contains("# 方案设置说明", plans.Single(plan => plan.Id == daily.Id).Source);
        var global = plans.Single(plan => plan.Id == BuiltInScriptPlans.GlobalConstantsId);
        Assert.Contains("$示例账号", global.Source);
        Assert.Empty(global.Constants);
        Assert.DoesNotContain(plans, plan => plan.Name == "并行调用两个子方案");
    }

    [Fact]
    public void UnconfiguredLegacyTemplateUpgradesToTheUserEditedExample()
    {
        using var files = new ScriptFileStore(_root);
        var legacy = BuiltInScriptPlans.Create()[0];
        legacy.Source = "日常操作,$主号,存档1\n日常操作,$主号,存档2\n关闭窗口,$主号";
        legacy.Constants["主号"] = "请绑定账号";
        files.SavePlan(legacy);
        File.WriteAllText(Path.Combine(_root, "plans", ".built-ins-v1"), "1");
        files.EnsureBuiltInPlans();
        var upgraded = files.ListPlans(out _).Single(plan => plan.Id == legacy.Id);
        Assert.Contains("日常操作,账号名称,存档1", upgraded.Source);
        Assert.Empty(upgraded.Constants);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LegacyTemplateUserCommentsAndRawConstantEditsAreNotOverwritten(bool editComment)
    {
        using var files = new ScriptFileStore(_root);
        var plan = BuiltInScriptPlans.Create()[0];
        plan.Source = "日常操作,$主号,存档1\n日常操作,$主号,存档2\n关闭窗口,$主号";
        plan.Constants["主号"] = "请绑定账号";
        if (editComment) plan.Source = "# 用户自己的说明\n" + plan.Source;
        else plan.ConstantsSource = "主号=虚构甲";
        files.SavePlan(plan);
        File.WriteAllText(Path.Combine(_root, "plans", ".built-ins-v1"), "1");
        files.EnsureBuiltInPlans();
        var saved = files.ListPlans(out _).Single(candidate => candidate.Id == plan.Id);
        Assert.Contains(plan.Source, saved.Source);
        Assert.Equal(plan.ConstantsSource, saved.ConstantsSource);
    }
    public void Dispose() => Directory.Delete(_root, true);
}
