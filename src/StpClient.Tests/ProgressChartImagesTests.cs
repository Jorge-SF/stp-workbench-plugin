using System;
using System.Linq;
using System.Threading;
using Maptek.Vulcan.Stp.Bridge.Contracts;
using Maptek.Vulcan.Stp.Planner;
using Maptek.Vulcan.Stp.Views.Planner;
using Xunit;

namespace Maptek.Vulcan.Stp.Client.Tests
{
    /// <summary>The Excel "Gráficas" pictures: one PNG per daily card, then one per total card.</summary>
    public sealed class ProgressChartImagesTests
    {
        private static void OnSta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception error)
                {
                    failure = error;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
        }

        [Fact]
        public void Cards_render_as_png_daily_first()
        {
            OnSta(() =>
            {
                using (var session = DesignSnapshot.CreateSession())
                {
                    session.ShowActualProgress = true;
                    var row = session.Rows.First(r => r.Segments.Any(s => s.Status != SegmentStatus.Completed));
                    string day = row.Segments.First(s => s.Status != SegmentStatus.Completed).Date;
                    Assert.True(session.SetActualProgress(row.ActivityId, day, 3).Ok);

                    var charts = ProgressCharts.Build(session.ActualProgressSummary, session.ShowActualProgress);
                    Assert.NotEmpty(charts);
                    var images = ProgressChartImages.Render(charts);
                    int expected = Math.Min(ExcelExportRequest.MaxChartImages, charts.Count * 2);
                    Assert.Equal(expected, images.Count);
                    Assert.Equal(charts.Select(c => c.Type).Concat(charts.Select(c => c.Type)).Take(expected), images.Select(i => i.Title));
                    byte[] magic = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
                    Assert.All(images, i => Assert.Equal(magic, i.Png.Take(8)));

                    // The pictures pass the bridge's checks.
                    var request = new ExcelExportRequest { ProjectKey = session.Snapshot.ProjectKey };
                    request.Days.Add(new ExcelExportDay(day, "d"));
                    foreach (var image in images) request.ChartImages.Add(image);
                    request.Validate();
                }
            });
            Assert.Empty(ProgressChartImages.Render(Array.Empty<ProgressChartModel>()));
        }
    }
}
