using System.Text.Json;

namespace Tabbar;

internal static class QuotaTest
{
    internal static void Run()
    {
        var report = new List<string>();
        void Check(bool passed, string name) { if (!passed) throw new InvalidOperationException(name); report.Add("PASS: " + name); }
        JsonElement Parse(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }
        try
        {
            var codex = QuotaReader.ParseCodex(Parse("""{"rateLimits":{"primary":{"usedPercent":25,"windowDurationMins":300,"resetsAt":2000000000},"secondary":{"usedPercent":80,"windowDurationMins":10080}}}"""));
            Check(codex.Remaining == 20 && codex.Period == "Uke" && codex.Detail!.Contains("75%"), "Codex selects limiting window and retains both windows");
            Check(QuotaReader.ParseCodex(Parse("""{"rateLimits":{"primary":null,"secondary":null,"credits":{"unlimited":true}}}""")).Remaining is null, "Unlimited extra credits do not manufacture subscription quota");
            Check(QuotaReader.ParseCodex(Parse("""{"rateLimits":{"individualLimit":{"remainingPercent":42,"resetsAt":2000000000}}}""")).Remaining == 42, "Codex individual allowance is supported");
            var observed = new DateTimeOffset(2026, 11, 4, 12, 30, 0, TimeSpan.Zero);
            JsonElement CopilotAt(string resetDate) => Parse(JsonSerializer.Serialize(new { quotaSnapshots = new { premium_interactions = new { entitlementRequests = 300, usedRequests = 225, remainingPercentage = 25, resetDate } } }));
            var copilot = QuotaReader.ParseCopilot(CopilotAt(observed.ToString("O")), observed);
            var expectedReset = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
            Check(copilot.Remaining == 25 && copilot.ResetsAt == expectedReset, "Copilot fetch timestamp is not used as the monthly reset");
            Check(QuotaReader.ParseCopilot(CopilotAt(observed.AddMinutes(5).ToString("O")), observed.AddMinutes(5)).ResetsAt == expectedReset,
                "Refreshing Copilot usage does not move the reset time");
            foreach (var resetDate in new[] { "", "invalid", "2030-01-01T00:00:00Z" })
                Check(QuotaReader.ParseCopilot(CopilotAt(resetDate), observed).ResetsAt == expectedReset, "Copilot monthly reset is independent of unreliable CLI resetDate: " + resetDate);
            Check(QuotaReader.ParseCopilot(CopilotAt(""), new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero)).ResetsAt == new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), "Copilot reset handles December to January");
            Check(QuotaReader.ParseCopilot(CopilotAt(""), new DateTimeOffset(2028, 2, 29, 23, 59, 59, TimeSpan.Zero)).ResetsAt == new DateTimeOffset(2028, 3, 1, 0, 0, 0, TimeSpan.Zero), "Copilot reset handles leap February");
            Check(QuotaReader.ParseCopilot(CopilotAt(""), new DateTimeOffset(2026, 12, 1, 0, 30, 0, TimeSpan.FromHours(1))).ResetsAt == expectedReset,
                "Copilot uses UTC month boundaries rather than local calendar dates");
            Check(QuotaReader.ParseCopilot(CopilotAt(""), expectedReset).ResetsAt == expectedReset.AddMonths(1), "Copilot rolls to the next period at exactly midnight UTC");
            var oslo = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
            Check(TimeZoneInfo.ConvertTime(expectedReset, oslo).Hour == 1 && TimeZoneInfo.ConvertTime(expectedReset.AddMonths(7), oslo).Hour == 2,
                "Monthly UTC reset displays correctly in Norwegian winter and summer time");
            Check(copilot.ResetNote!.Contains("00:00 UTC"), "Copilot tooltip explains the calendar-derived reset");
            Check(QuotaReader.ParseCopilot(Parse("""{"quotaSnapshots":{"premium_interactions":{"entitlementRequests":-1,"resetDate":"2026-11-04T12:30:00Z"}}}"""), observed).ResetsAt is null,
                "Unlimited Copilot quota does not show a misleading reset time");
            Check(QuotaReader.ParseCopilot(Parse("""{"quotaSnapshots":{"premium_interactions":{"entitlementRequests":-1}}}""")).Unlimited, "Copilot unlimited entitlement is distinct from unknown");
            Check(QuotaReader.ParseCopilot(Parse("""{"quotaSnapshots":{}}""")).Remaining is null, "Missing quotas stay unavailable, not exhausted");
            Check(QuotaReader.ParseCopilot(Parse("""{"quotaSnapshots":{"premium_interactions":{"remainingPercentage":0}}}""")).Remaining == 0, "Zero remaining is a valid exhausted quota");
            var custom = new GaugeSettings { AutoRotate = false, SlideSeconds = 12, RefreshMinutes = 2,
                Codex = new() { ExecutablePath = Environment.ProcessPath!, ProfileDirectory = AppContext.BaseDirectory } };
            var copied = custom.Copy(); copied.Codex.ProfileDirectory = "changed";
            Check(custom.Codex.ProfileDirectory == AppContext.BaseDirectory, "Editing settings does not mutate saved provider configuration");
            var restored = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(new Settings { Gauges = custom, IconHeight = 64 }))!;
            Check(!restored.Gauges.AutoRotate && restored.Gauges.SlideSeconds == 12 && restored.Gauges.Codex.ExecutablePath == Environment.ProcessPath && restored.IconHeight == 64, "Slideshow, icons and provider settings survive persistence");
            var login = QuotaReader.LoginStartInfo(false, custom.Codex);
            Check(login.ArgumentList.SequenceEqual(new[] { "login" }) && login.Environment["CODEX_HOME"] == custom.Codex.ProfileDirectory && !login.UseShellExecute,
                "Login uses the selected CLI and profile without shell interpolation or stored passwords");
            Check(QuotaReader.FindExecutable(false, new() { ExecutablePath = Path.Combine(AppContext.BaseDirectory, "nonexistent.exe") }) is null,
                "An invalid explicit CLI path does not silently use a different account installation");
            var legacy = JsonSerializer.Deserialize<Settings>("{}");
            Check(legacy!.Gauges.AutoRotate && legacy.Gauges.SlideSeconds == 8 && legacy.IconHeight == 32, "Existing settings gain automatic slideshow defaults and retain 32px icons");
            foreach (var width in new[] { 95, 145, 295 })
            {
                using var panel = new Panel { Size = new Size(width, 170), BackColor = Color.FromArgb(20, 26, 36) };
                using var slideshow = new GaugeSlideshow(custom, false);
                panel.Controls.Add(slideshow);
                panel.CreateControl();
                Check(slideshow.SelectedIndex == 0 && slideshow.Memory.Visible, "Slideshow starts with RAM");
                for (var slide = 0; slide < 3; slide++)
                {
                    if (slide > 0) slideshow.NextButton.PerformClick();
                    slideshow.Codex.SetPreview(codex); slideshow.Copilot.SetPreview(copilot);
                    Check(slideshow.SelectedIndex == slide && new Control[] { slideshow.Memory, slideshow.Codex, slideshow.Copilot }.Count(c => c.Visible) == 1,
                        $"Exactly one gauge is visible on slide {slide} at width {width}");
                    using var bitmap = new Bitmap(panel.Width, panel.Height);
                    panel.DrawToBitmap(bitmap, panel.ClientRectangle);
                    bitmap.Save(Path.Combine(AppContext.BaseDirectory, $"slideshow-{width}-{slide}.png"));
                }
                slideshow.Advance(1);
                Check(slideshow.SelectedIndex == 0, "Next wraps from Copilot to RAM");
                slideshow.PreviousButton.PerformClick();
                Check(slideshow.SelectedIndex == 2 && slideshow.Height == 170 && slideshow.Width == width
                    && slideshow.PreviousButton.Visible && slideshow.NextButton.Visible, "Previous wraps and navigation fits below the single gauge");
            }
            using (var dialog = new GaugeSettingsDialog(new()))
            {
                dialog.Show(); Application.DoEvents();
                using var preview = new Bitmap(dialog.Width, dialog.Height);
                dialog.DrawToBitmap(preview, new Rectangle(Point.Empty, dialog.Size));
                preview.Save(Path.Combine(AppContext.BaseDirectory, "gauge-settings.png"));
                dialog.Close();
            }
            var testDirectory = Path.Combine(Path.GetTempPath(), "Tabbar-icons-" + Guid.NewGuid().ToString("N"));
            foreach (var height in new[] { 16, 64, 128 })
            {
                var toolbarSettings = new Settings { Width = 220, IconHeight = height, Gauges = new() { AutoRotate = false },
                    Scripts = [new() { Name = "Eksempel", Path = "https://example.com", Kind = ShortcutKind.Website }] };
                using var form = new ToolbarForm(new SettingsStore(testDirectory), toolbarSettings, false);
                form.SetBounds(0, 0, 220, 800); form.Show(); Application.DoEvents();
                var card = form.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>().Single();
                Check(card.Image!.Height == height && card.Height == height + 76, $"Shortcut icon and card heights follow the {height}px setting");
                using var preview = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(preview, form.ClientRectangle);
                preview.Save(Path.Combine(AppContext.BaseDirectory, $"shortcut-icons-{height}.png"));
                form.Close();
            }
            using (var rotating = new GaugeSlideshow(new() { SlideSeconds = 3 }, false))
            {
                rotating.CreateControl();
                // Keep the test independent of the user's pointer position.
                rotating.Location = new Point(-10000, -10000);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < 3300) { Application.DoEvents(); Thread.Sleep(10); }
                Check(rotating.SelectedIndex == 1, "Slideshow advances automatically on its configured interval");
                rotating.Configure(new() { AutoRotate = false, SlideSeconds = 3 });
                watch.Restart();
                while (watch.ElapsedMilliseconds < 3300) { Application.DoEvents(); Thread.Sleep(10); }
                Check(rotating.SelectedIndex == 1, "Automatic rotation can be disabled");
            }
            // Isolated quota calls do not touch the active toolbar or create a model turn.
            var tasks = new[] { Task.Run(() => QuotaReader.ReadAsync(false, CancellationToken.None)), Task.Run(() => QuotaReader.ReadAsync(true, CancellationToken.None)) };
            Task.WaitAll(tasks);
            for (var i = 0; i < tasks.Length; i++)
            {
                var value = tasks[i].Result;
                report.Add($"LIVE {(i == 0 ? "Codex" : "Copilot")}: remaining={value.Remaining?.ToString() ?? "unknown"}; unlimited={value.Unlimited}; period={value.Period}; detail={value.Detail}");
            }
        }
        catch (Exception e) { report.Add("FAIL: " + e.Message); Environment.ExitCode = 1; }
        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "quota-test-results.txt"), report);
    }
}
