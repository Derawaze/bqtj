using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using BqtjLauncher.Domain;
using BqtjLauncher.Infrastructure;
using BqtjLauncher.Runtime.Flash;

namespace BqtjLauncher.Application.Tests;

/// <summary>官方流程使用合成菜单/像素，验证实际执行器选档、保存观察与确认退出，不读取用户游戏。</summary>
public sealed class OfficialScriptExecutorTests
{
    private static readonly string[] ExpectedActions = ["读取存档", "存档8", "助理", "日常操作复制", "运行", "确定运行", "退出存档", "确定退出"];
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task ReadsNamesForAllEightCardsInRowOrder(int incompleteFrames)
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-flow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new SqliteGameProfileRepository(Path.Combine(root, "fake.db"));
            await using var launcher = new LauncherModule(repository, new FakeRuntime());
            await launcher.InitializeAsync();
            var profile = await launcher.CreateProfileAsync("虚构流程账号");
            var session = (await launcher.StartAutomationSessionAsync(profile.Id))!;
            var vision = new MenuVision { IncompleteFrames = incompleteFrames };
            var executor = new OfficialScriptExecutor(vision);
            var saves = await executor.ReadSavesAsync(session, _ => { }, default);
            Assert.Equal(Enumerable.Range(1, 8), saves.Select(save => save.Number));
            Assert.Equal(Enumerable.Range(1, 8).Select(number => number == 1 ? "123456 lv.99" : "测试档" + number + " lv.99"), saves.Select(save => save.Name));
            Assert.Equal("selection", vision.State);
            Assert.Equal("读取存档", Assert.Single(vision.Actions));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task IncompleteMenuWaitCanStopWithoutClickingAnySave()
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new SqliteGameProfileRepository(Path.Combine(root, "fake.db"));
            await using var launcher = new LauncherModule(repository, new FakeRuntime());
            await launcher.InitializeAsync();
            var profile = await launcher.CreateProfileAsync("虚构未绘制菜单");
            var session = (await launcher.StartAutomationSessionAsync(profile.Id))!;
            var vision = new MenuVision { IncompleteFrames = int.MaxValue };
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new OfficialScriptExecutor(vision).ReadSavesAsync(session, _ => { }, cancel.Token));
            Assert.Equal(0, vision.SelectedSave);
            Assert.Equal("读取存档", Assert.Single(vision.Actions));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    [Fact]
    public async Task DailyConfirmsHeaderCheckWithLastSaveRowOutsideViewportThenExits()
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-flow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new SqliteGameProfileRepository(Path.Combine(root, "fake.db"));
            await using var launcher = new LauncherModule(repository, new FakeRuntime());
            await launcher.InitializeAsync();
            var profile = await launcher.CreateProfileAsync("虚构流程账号");
            var session = (await launcher.StartAutomationSessionAsync(profile.Id))!;
            var vision = new MenuVision { IncompleteFrames = 2 };
            var executor = new OfficialScriptExecutor(vision);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var script = new ScriptDefinition { Id = "official-daily", Entry = "daily", Flow = [new() { Id = "daily", Type = "officialDaily" }] };
            var timer = Stopwatch.StartNew();
            await executor.ExecuteAsync(session, script, new() { ProfileId = profile.Id, SaveNumber = 8 },
                SpeedMultiplier.Create(2), (_, _) => { }, timeout.Token);
            Assert.True(timer.Elapsed >= TimeSpan.FromSeconds(3));
            Assert.Equal(8, vision.SelectedSave);
            Assert.Equal("saved", vision.State);
            Assert.DoesNotContain("保存存档", vision.Actions);
            await executor.ExitSaveAsync(session, (_, _) => { }, timeout.Token);
            Assert.Equal("home", vision.State);
            Assert.Equal(ExpectedActions, vision.Actions);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private sealed class FakeRuntime : IAutomationGameRuntime
    {
        public Task<GamePageResolution> ResolveGamePageAsync() => throw new NotSupportedException();
        public Task<IGameSession> StartAsync(GameProfile profile, CancellationToken token = default) => Task.FromResult<IGameSession>(new FakeSession(profile.Id));
        public Task<IGameSession> StartAutomationAsync(GameProfile profile, CancellationToken token = default) => StartAsync(profile, token);
    }
    private sealed class FakeSession(Guid profile) : IAutomationGameSession
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid Id { get; } = Guid.NewGuid();
        public Guid ProfileId => profile;
        public Task Completion => _completion.Task;
        public void Activate() { }
        public Task CloseAsync(CancellationToken token = default) { _completion.TrySetResult(); return Task.CompletedTask; }
        public bool WindowLocked { get; private set; } = true;
        public Task AcquireWindowLockAsync(CancellationToken token = default) => System.Threading.Tasks.Task.CompletedTask;
        public Task ReleaseWindowLockAsync(CancellationToken token = default) { WindowLocked = false; return System.Threading.Tasks.Task.CompletedTask; }
        public Task<AutomationWindowTarget> GetTargetAsync(CancellationToken token = default)
            => Task.FromResult(new AutomationWindowTarget(Id, profile, 1, 2, 3, 950, 600));
        public Task<SpeedMultiplier> ApplySpeedAsync(SpeedMultiplier speed, CancellationToken token = default) => Task.FromResult(SpeedMultiplier.Original);
    }

    private sealed class MenuVision : IScriptVision
    {
        public int IncompleteFrames { get; init; }
        private int _menuFrames;
        public string State { get; private set; } = "home";
        public List<string> Actions { get; } = [];
        public int SelectedSave { get; private set; }
        private long _snapshot;
        private long _captured;
        private int _progress;
        public Task BeginAsync(CancellationToken token) => Task.CompletedTask;
        public Task EndAsync() => Task.CompletedTask;
        public Task ReleaseTargetAsync(AutomationWindowTarget target) => Task.CompletedTask;
        public Task<ScriptRect?> FindImageAsync(AutomationWindowTarget target, ScriptRect roi, byte[] image, CancellationToken token) => throw new NotSupportedException();

        public Task<ScriptVisionFrame> ReadAsync(AutomationWindowTarget target, ScriptRect roi, CancellationToken token, long snapshotId = 0, ScriptTextStyle style = ScriptTextStyle.Normal)
        {
            if (snapshotId == 0) { _snapshot++; _captured = Stopwatch.GetTimestamp(); }
            var lines = new List<ScriptText>();
            void Add(string text, ScriptRect box) { if (style == ScriptTextStyle.Green || Intersects(roi, box)) lines.Add(new(text, box, 0.99)); }
            if (style == ScriptTextStyle.Green) Add("保存存档", new(42, 9, 180, 42));
            else if (State == "home") Add("读取存档", new(410, 550, 100, 25));
            else if (State == "selection")
            {
                Add("读取存档", new(400, 150, 160, 30));
                if (roi.Width <= 182 && roi.Height <= 40)
                {
                    var row = (roi.Y - 198) / 61;
                    var number = row * 2 + (roi.X > 400 ? 2 : 1);
                    if (style != ScriptTextStyle.SaveName) Add("角色通关", new(roi.X + 130, roi.Y, 44, 12));
                    Add((number == 1 ? "123456" : "测试档" + number) + " 1v.99", new(roi.X + 5, roi.Y + 8, 110, 12));
                    if (style != ScriptTextStyle.SaveName) Add("2026-01-01 12:00:00", new(roi.X + 5, roi.Y + 30, 130, 8));
                }
            }
            else if (State == "world") Add("助理", new(100, 45, 50, 24));
            else if (State == "assistant") Add("日常操作复制", new(130, 180, 140, 24));
            else if (State == "selected")
            {
                Add("日常操作复制", new(350, 130, 140, 24)); Add("运行", new(530, 490, 60, 24));
            }
            else if (State == "prompt")
            {
                Add("是否开始运行《日常操作复制》？", new(290, 180, 380, 40)); Add("确定", new(350, 300, 60, 30));
            }
            else if (State is "running" or "saved")
            {
                if (roi == new ScriptRect(759, 154, 182, 420))
                {
                    _progress++;
                    var done = _progress < 3 ? _progress - 1 : 23;
                    Add("√日常操作复制" + done + "/23", new(786, 170, 122, 17));
                    // 实际完成画面末行在侧栏视口外，只能看到标题23/23及左侧绿勾。
                    if (done == 23) State = "saved";
                }
                else Add("退出存档", new(400, 300, 100, 25));
            }
            else if (State == "exit")
            {
                Add("是否退出存档", new(300, 180, 200, 30)); Add("确定", new(360, 300, 60, 30));
            }
            return Task.FromResult(new ScriptVisionFrame(_snapshot, _captured, lines));
        }

        public Task<ScriptPixels> PixelsAsync(AutomationWindowTarget target, ScriptRect roi, long snapshot, CancellationToken token)
        {
            using var bitmap = new Bitmap(roi.Width, roi.Height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Black);
                if (roi.Width == 950 && ++_menuFrames > IncompleteFrames)
                {
                    using var border = new Pen(Color.FromArgb(80, 80, 80));
                    for (var row = 0; row < 4; row++) for (var column = 0; column < 2; column++)
                        graphics.DrawRectangle(border, column == 0 ? 276 : 483, 191 + row * 61, 199, 54);
                }
                else
                {
                    using var check = new Pen(Color.Lime);
                    graphics.DrawLines(check, [new Point(29, 24), new Point(32, 28), new Point(38, 16)]);
                }
            }
            var data = bitmap.LockBits(new(0, 0, roi.Width, roi.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var pixels = new byte[roi.Width * roi.Height * 3];
            try { for (var y = 0; y < roi.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * roi.Width * 3, roi.Width * 3); }
            finally { bitmap.UnlockBits(data); }
            return Task.FromResult(new ScriptPixels(roi.Width, roi.Height, pixels));
        }

        public Task ClickAsync(AutomationWindowTarget target, int x, int y, CancellationToken token)
        {
            switch (State)
            {
                case "home": Actions.Add("读取存档"); State = "selection"; break;
                case "selection":
                    SelectedSave = (y - 218) / 61 * 2 + (x > 480 ? 2 : 1); Actions.Add("存档" + SelectedSave); State = "world"; break;
                case "world": Actions.Add("助理"); State = "assistant"; break;
                case "assistant": Actions.Add("日常操作复制"); State = "selected"; break;
                case "selected": Actions.Add("运行"); State = "prompt"; break;
                case "prompt": Actions.Add("确定运行"); State = "running"; break;
                case "saved": Actions.Add("退出存档"); State = "exit"; break;
                case "exit": Actions.Add("确定退出"); State = "home"; break;
                default: throw new InvalidOperationException("合成状态不应重复点击");
            }
            return Task.CompletedTask;
        }
        private static bool Intersects(ScriptRect a, ScriptRect b) => a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
    }
}
