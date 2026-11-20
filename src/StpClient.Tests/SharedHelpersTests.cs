using System.Linq;
using Maptek.Vulcan.Stp.Planner;
using Maptek.Vulcan.Stp.Views.Planner;
using Xunit;

namespace Maptek.Vulcan.Stp.Client.Tests
{
    /// <summary>StpPlanner's one RelayCommand, the type palette and the display fields the screen binds to.</summary>
    public class SharedHelpersTests
    {
        [Fact]
        public void RelayCommand_respects_CanExecute_and_raises_its_change()
        {
            bool enabled = false;
            int runs = 0, raised = 0;
            var command = new RelayCommand(() => runs++, () => enabled);
            command.CanExecuteChanged += (s, e) => raised++;

            Assert.False(command.CanExecute(null));
            command.Execute(null);
            Assert.Equal(0, runs);

            enabled = true;
            command.RaiseCanExecuteChanged();
            Assert.Equal(1, raised);
            command.Execute(null);
            Assert.Equal(1, runs);
        }

        [Fact]
        public void RelayCommand_passes_its_parameter()
        {
            object seen = null;
            var command = new RelayCommand(p => seen = p, p => p is string);
            Assert.False(command.CanExecute(3));
            command.Execute("Voladura");
            Assert.Equal("Voladura", seen);
            Assert.False(RelayCommand.Disabled().CanExecute(null));
        }

        [Fact]
        public void Palette_keeps_mockup_colours_and_gives_other_types_distinct_ones()
        {
            var colors = TypePalette.Assign(new[] { "Voladura", "Zeta", "Alfa", "Barrenación", "Alfa" });
            Assert.Equal("#4F86C6", colors["Voladura"]);
            Assert.Equal("#3B78C4", colors["Barrenación"]);
            Assert.Equal(4, colors.Count);
            Assert.Equal(4, colors.Values.Distinct().Count());
            Assert.Equal(colors, TypePalette.Assign(new[] { "Alfa", "Barrenación", "Zeta", "Voladura" }));
        }

        [Fact]
        public void Palette_reuses_colours_past_its_size()
        {
            var colors = TypePalette.Assign(Enumerable.Range(0, 40).Select(i => "T" + i.ToString("00")));
            Assert.Equal(40, colors.Count);
            Assert.All(colors.Values, c => Assert.Matches("^#[0-9A-F]{6}$", c));
        }

        [Fact]
        public void Screen_display_fields_come_from_the_session()
        {
            using var session = DesignSnapshot.CreateSession();
            Assert.All(session.TypeChips, c => Assert.Equal(session.TypeColor(c.Type), c.Color));
            Assert.Equal(session.TypeChips.Select(c => c.Type), session.TypeChips.Select(c => c.Name));

            var rez = session.Capacity.Single(r => r.Type == "Rezagado");
            Assert.Equal("2,500", rez.ObjectiveText);
            Assert.Equal(session.TypeColor("Rezagado"), rez.Color);
            Assert.Equal(14, rez.Cells.Count);
            Assert.Equal("·", rez.Cells[0].Text);
            Assert.Equal("2.9k", rez.Cells[10].Text);   // REZ-1400-02 1220 + REZ-1425-01 1700
            Assert.True(rez.Cells[10].IsOver);
            Assert.Equal("—", session.Capacity.Single(r => r.Type == "Servicios").ObjectiveText);
            Assert.Equal("16", session.Days[0].DayOfMonth);
        }
    }
}
