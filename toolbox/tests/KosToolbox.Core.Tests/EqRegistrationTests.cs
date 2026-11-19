using KosToolbox.Core.Audio;
using Microsoft.Win32;
using Xunit;

namespace KosToolbox.Core.Tests;

public class EqRegistrationTests
{
    const string Other = "{EACD2258-FCAC-4FF4-B36D-419E924A6D79}", OtherEfx = "{EC1CC9CE-FAED-4822-828A-82A81A6F018F}";
    const string DriverLfx = "{C9453E73-8C5C-4463-9984-AF8BAB2F5447}", DriverGfx = "{13AB3EBD-137E-4903-9D89-60BE8277FD17}";

    static Dictionary<string, FxValue> Fx(params (int Pid, FxValue Value)[] values) =>
        values.ToDictionary(v => EqRegistration.FxKey(v.Pid), v => v.Value, StringComparer.OrdinalIgnoreCase);

    static FxValue? After(EqFxPlan plan, string name) => plan.Changes.Single(c => c.Name == name).After;

    [Fact]
    public void ReplacingTheOtherEqualizerPutsBackWhatItWrappedAndTakesTheEndpointSlot()
    {
        // This PC's headset as the other equalizer left it, with its record of the originals.
        var fx = Fx((5, FxValue.String(Other)), (6, FxValue.String(DriverGfx)), (7, FxValue.String(OtherEfx)));
        var record = new Dictionary<string, string>
        {
            [EqRegistration.FxKey(1)] = "!VALUE", [EqRegistration.FxKey(5)] = DriverLfx, [EqRegistration.FxKey(6)] = DriverGfx, [EqRegistration.FxKey(7)] = "!VALUE",
        };
        var plan = EqRegistration.Plan(fx, record);
        Assert.Null(plan.Refused);
        Assert.True(plan.ReplacesOther);
        Assert.Equal(DriverLfx, After(plan, EqRegistration.FxKey(5))!.Text);
        Assert.Equal(EqRegistration.ClsidText, After(plan, EqRegistration.FxKey(7))!.Text);
        Assert.Equal(EqRegistration.ModeDefault, After(plan, EqRegistration.ModesKey(7))!.Text);
        Assert.DoesNotContain(plan.Changes, c => c.Name == EqRegistration.FxKey(6)); // the driver's own effect stays
    }

    [Fact]
    public void WithoutTheOtherEqualizersRecordItsSlotIsEmptied()
    {
        var plan = EqRegistration.Plan(Fx((5, FxValue.String(Other))), null);
        Assert.True(plan.ReplacesOther);
        Assert.Null(After(plan, EqRegistration.FxKey(5)));
    }

    [Fact]
    public void ACleanEndpointGetsOnlyTheEndpointEffect()
    {
        var plan = EqRegistration.Plan(Fx((5, FxValue.String(DriverLfx))), null);
        Assert.False(plan.ReplacesOther);
        Assert.Equal([EqRegistration.FxKey(7), EqRegistration.ModesKey(7)], plan.Changes.Select(c => c.Name));
        Assert.All(plan.Changes, c => Assert.Null(c.Before));
    }

    [Fact]
    public void ADriversOwnEndpointEffectIsRefused()
    {
        Assert.NotNull(EqRegistration.Plan(Fx((7, FxValue.String(DriverGfx))), null).Refused);
        Assert.NotNull(EqRegistration.Plan(Fx((15, FxValue.Multi([DriverGfx]))), null).Refused);
        // The other equalizer's entry in a composite list comes out; nothing of the driver's left there, so it goes on.
        var plan = EqRegistration.Plan(Fx((15, FxValue.Multi([OtherEfx]))), null);
        Assert.Null(plan.Refused);
        Assert.Null(After(plan, EqRegistration.FxKey(15)));
    }

    [Fact]
    public void UndoPutsBackOnlyWhatStillHoldsWhatKosWrote()
    {
        var fx = Fx((5, FxValue.String(Other)), (7, FxValue.String(OtherEfx)));
        var plan = EqRegistration.Plan(fx, new Dictionary<string, string> { [EqRegistration.FxKey(5)] = DriverLfx });
        // Applied, then a driver update rewrote slot 5; the modes key was never written (a halfway failure).
        var now = Fx((5, FxValue.String(DriverGfx)), (7, FxValue.String(EqRegistration.ClsidText)));
        var changed = new List<string>();
        var undo = EqRegistration.Undo(plan.Changes, now, changed);
        Assert.Equal([EqRegistration.FxKey(5)], changed);
        var efx = Assert.Single(undo);
        Assert.Equal(OtherEfx, efx.After!.Text);
    }

    [Fact]
    public void EndpointIdsMapToTheirKeyAndFile()
    {
        const string id = "{0.0.0.00000000}.{3718CD90-BA40-40AE-9DFC-93593F73D916}";
        Assert.Equal(("Render", "{3718cd90-ba40-40ae-9dfc-93593f73d916}"), EqRegistration.Endpoint(id));
        Assert.Equal("Capture", EqRegistration.Endpoint("{0.0.1.00000000}.{3718cd90-ba40-40ae-9dfc-93593f73d916}").Flow);
        Assert.EndsWith(@"Eq\3718cd90-ba40-40ae-9dfc-93593f73d916.eq", EqSettingsFile.PathFor(id));
        Assert.Equal("", EqSettingsFile.FileName(@"..\..\x"));
        Assert.Throws<ArgumentException>(() => EqRegistration.Endpoint(@"{0.0.0.00000000}.{..\..\Windows}"));
    }

    [Fact]
    public void SettingsFileHoldsThePreampAndEnabledBandsAndRefusesBigBoosts()
    {
        var p = new EqProfile
        {
            Name = "Bass\r\npreamp 30",
            Preamp = -3.5,
            Bands =
            {
                new EqBand { Type = EqFilterType.LowShelf, Frequency = 105, Gain = 4.25, Q = 0.7 },
                new EqBand { Type = EqFilterType.Peak, Frequency = 1000, Gain = 6, Q = 1.41, Enabled = false },
            },
        };
        Assert.Equal("# KOS Equalizer: Bass??preamp 30\r\npreamp -3.5\r\nlowshelf 105 4.25 0.7\r\n", EqSettingsFile.Format(p));
        Assert.Null(EqSettingsFile.Problem(p));
        p.Bands.Add(new EqBand { Type = EqFilterType.Peak, Frequency = 1000, Gain = 20, Q = 1 });
        p.Preamp = 10;
        Assert.NotNull(EqSettingsFile.Problem(p));
    }

    [Fact]
    public void FxValuesCompareByKindAndText()
    {
        Assert.True(FxValue.Same(FxValue.String(Other.ToLowerInvariant()), FxValue.String(Other)));
        Assert.False(FxValue.Same(FxValue.String(Other), FxValue.Multi([Other])));
        Assert.True(FxValue.Same(null, null));
        Assert.Equal([Other, OtherEfx], FxValue.Multi([Other, OtherEfx]).Items);
        Assert.Equal(RegistryValueKind.MultiString, FxValue.Multi([Other]).Kind);
    }
}
