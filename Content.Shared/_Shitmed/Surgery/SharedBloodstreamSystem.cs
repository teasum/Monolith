// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared._Shitmed.CCVar;
using Content.Shared._Shitmed.Medical.Surgery.Traumas.Components;
using Content.Shared._Shitmed.Medical.Surgery.Wounds;
using Content.Shared._Shitmed.Medical.Surgery.Wounds.Components;
using Content.Shared._Shitmed.Medical.Surgery.Wounds.Systems;
using Content.Shared.Body.Part;
using Content.Goobstation.Maths.FixedPoint;
using Content.Shared.Popups;
using JetBrains.Annotations;
using Robust.Shared.Configuration;
using Robust.Shared.Utility;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Timing;
using Content.Shared._Shitmed.Medical.Surgery.Consciousness.Systems;
using Content.Shared.Body.Components;

// ReSharper disable once CheckNamespace
namespace Content.Shared.Body.Systems;

[UsedImplicitly]
public abstract partial class SharedBloodstreamSystem : EntitySystem
{
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly WoundSystem _wound = default!;
    [Dependency] private readonly ConsciousnessSystem _consciousness = default!;
    [Dependency] private readonly SharedBodySystem _body = default!;

    private void InitializeWounds()
    {
        SubscribeLocalEvent<BleedInflicterComponent, WoundSeverityPointChangedEvent>(OnBleedInflicterSeverityUpdate);
        SubscribeLocalEvent<BleedRemoverComponent, WoundSeverityPointChangedEvent>(OnBleedRemoverSeverityUpdate);
        SubscribeLocalEvent<BleedInflicterComponent, WoundHealAttemptEvent>(OnWoundHealAttempt);
        SubscribeLocalEvent<BleedInflicterComponent, WoundAddedEvent>(OnWoundAdded);
    }

    private static readonly TimeSpan WoundBleedUpdateInterval = TimeSpan.FromSeconds(1);
    private TimeSpan _nextWoundBleedUpdate;

    private void UpdateWounds(float frameTime)
    {
        if (_timing.CurTime < _nextWoundBleedUpdate)
            return;

        _nextWoundBleedUpdate = _timing.CurTime + WoundBleedUpdateInterval;

        var bleedsQuery = EntityQueryEnumerator<BleedInflicterComponent>();
        while (bleedsQuery.MoveNext(out var ent, out var bleeds))
        {
            var canBleed = CanWoundBleed(ent, bleeds) && bleeds.BleedingAmount > 0;
            if (canBleed != bleeds.IsBleeding)
            {
                bleeds.IsBleeding = canBleed;
                Dirty(ent, bleeds);
            }

            if (!bleeds.IsBleeding || bleeds.Scaling >= bleeds.ScalingLimit)
                continue;

            var start = bleeds.ScalingStartsAt;
            var end = bleeds.ScalingFinishesAt;
            if (end <= start)
                continue;

            var progress = (_timing.CurTime - start).TotalSeconds / (end - start).TotalSeconds;
            if (progress <= 0)
                continue;

            var target = FixedPoint2.New(1) + (bleeds.ScalingLimit - 1) * FixedPoint2.New(Math.Min(progress, 1.0));
            var newScaling = FixedPoint2.Clamp(target, bleeds.Scaling, bleeds.ScalingLimit);
            if (newScaling == bleeds.Scaling)
                continue;

            bleeds.Scaling = newScaling;
            Dirty(ent, bleeds);
        }
    }

    /// <summary>
    /// Add a bleed-ability modifier on woundable
    /// </summary>
    public bool TryAddBleedModifier(
        EntityUid woundable,
        string identifier,
        int priority,
        bool canBleed,
        bool force = false,
        WoundableComponent? woundableComp = null)
    {
        if (!Resolve(woundable, ref woundableComp))
            return false;

        foreach (var woundEnt in _wound.GetWoundableWounds(woundable, woundableComp))
        {
            if (!TryComp<BleedInflicterComponent>(woundEnt, out var bleedsComp))
                continue;

            if (TryAddBleedModifier(woundEnt, identifier, priority, canBleed, bleedsComp))
                continue;

            if (!force)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Add a bleed-ability modifier
    /// </summary>
    public bool TryAddBleedModifier(
        EntityUid uid,
        string identifier,
        int priority,
        bool canBleed,
        BleedInflicterComponent? comp = null)
    {
        if (!Resolve(uid, ref comp))
            return false;

        if (!comp.BleedingModifiers.TryAdd(identifier, (priority, canBleed)))
            return false;

        Dirty(uid, comp);
        return true;
    }

    /// <summary>
    /// Remove a bleed-ability modifier from a woundable
    /// </summary>
    public bool TryRemoveBleedModifier(
        EntityUid uid,
        string identifier,
        bool force = false,
        WoundableComponent? woundable = null)
    {
        if (!Resolve(uid, ref woundable))
            return false;

        foreach (var woundEnt in _wound.GetWoundableWounds(uid, woundable))
        {
            if (!TryComp<BleedInflicterComponent>(woundEnt, out var bleedsComp))
                continue;

            if (TryRemoveBleedModifier(woundEnt, identifier, bleedsComp))
                continue;

            if (!force)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Remove a bleed-ability modifier
    /// </summary>
    public bool TryRemoveBleedModifier(
        EntityUid uid,
        string identifier,
        BleedInflicterComponent? comp = null)
    {
        if (!Resolve(uid, ref comp))
            return false;

        if (!comp.BleedingModifiers.Remove(identifier))
            return false;

        Dirty(uid, comp);
        return true;
    }

    /// <summary>
    /// Redact a modifiers meta data
    /// </summary>
    public bool ChangeBleedsModifierMetadata(
        EntityUid wound,
        string identifier,
        int priority,
        bool? canBleed,
        BleedInflicterComponent? bleeds = null)
    {
        if (!Resolve(wound, ref bleeds))
            return false;

        if (!bleeds.BleedingModifiers.TryGetValue(identifier, out var pair))
            return false;

        bleeds.BleedingModifiers[identifier] = (Priority: priority, CanBleed: canBleed ?? pair.CanBleed);
        return true;
    }

    /// <summary>
    /// Redact a modifiers meta data
    /// </summary>
    public bool ChangeBleedsModifierMetadata(
        EntityUid wound,
        string identifier,
        bool canBleed,
        int? priority,
        BleedInflicterComponent? bleeds = null)
    {
        if (!Resolve(wound, ref bleeds))
            return false;

        if (!bleeds.BleedingModifiers.TryGetValue(identifier, out var pair))
            return false;

        bleeds.BleedingModifiers[identifier] = (Priority: priority ?? pair.Priority, CanBleed: canBleed);
        return true;
    }

    /// <summary>
    /// Self-explanatory
    /// </summary>
    public bool CanWoundBleed(EntityUid uid, BleedInflicterComponent? comp = null)
    {
        if (!Resolve(uid, ref comp))
            return false;

        var nearestModifier = comp.BleedingModifiers.FirstOrNull();
        if (nearestModifier == null)
            return true;

        var lastCanBleed = true;
        var lastPriority = int.MinValue;
        foreach (var (_, pair) in comp.BleedingModifiers)
        {
            if (pair.Priority <= lastPriority)
                continue;

            lastPriority = pair.Priority;
            lastCanBleed = pair.CanBleed;
        }

        return lastCanBleed;
    }

    private void OnWoundAdded(EntityUid uid, BleedInflicterComponent component, ref WoundAddedEvent args)
    {
        if (!CanWoundBleed(uid, component)
            || args.Component.WoundSeverityPoint < component.SeverityThreshold
            || !args.Woundable.CanBleed)
            return;

        // wounds that BLEED will not HEAL.
        component.BleedingAmountRaw = (FixedPoint2)args.Component.WoundSeverityPoint * FixedPoint2.New(_cfg.GetCVar(SurgeryCVars.BleedingSeverityTrade));

        var formula = (float) (args.Component.WoundSeverityPoint / _cfg.GetCVar(SurgeryCVars.BleedsScalingTime) * component.ScalingSpeed);
        component.ScalingFinishesAt = _timing.CurTime + TimeSpan.FromSeconds(formula);
        component.ScalingStartsAt = _timing.CurTime;
        component.IsBleeding = true;

        Dirty(uid, component);
    }

    private void OnWoundHealAttempt(EntityUid uid, BleedInflicterComponent component, ref WoundHealAttemptEvent args)
    {
        if (args.IgnoreBlockers)
            return;

        if (component.IsBleeding)
            args.Cancelled = true;
    }

    private void OnBleedInflicterSeverityUpdate(EntityUid uid,
        BleedInflicterComponent component,
        ref WoundSeverityPointChangedEvent args)
    {
        if (!CanWoundBleed(uid, component)
            || !TryComp<WoundableComponent>(args.Component.HoldingWoundable, out var woundable)
            || !woundable.CanBleed
            || args.NewSeverity < component.SeverityThreshold
            || args.NewSeverity < args.OldSeverity)
            return;

        component.BleedingAmountRaw = (FixedPoint2)args.NewSeverity * FixedPoint2.New(_cfg.GetCVar(SurgeryCVars.BleedingSeverityTrade));

        var formula = (float) (args.NewSeverity / _cfg.GetCVar(SurgeryCVars.BleedsScalingTime) * component.ScalingSpeed);
        component.ScalingFinishesAt = _timing.CurTime + TimeSpan.FromSeconds(formula);
        component.ScalingStartsAt = _timing.CurTime;

        if (!component.IsBleeding)
        {
            component.ScalingLimit += 0.6;
            component.IsBleeding = true;
        }

        if (component.BleedingAmountRaw > 0)
        {
            component.Scaling = 1;
        }

        Dirty(uid, component);
    }

    public void OnBleedRemoverSeverityUpdate(EntityUid uid, BleedRemoverComponent component, ref WoundSeverityPointChangedEvent args)
    {
        var delta = args.NewSeverity - args.OldSeverity;
        if (delta < component.SeverityThreshold
            || !TryComp(uid, out WoundComponent? wound)
            || TerminatingOrDeleted(wound.HoldingWoundable)
            || !TryComp(wound.HoldingWoundable, out WoundableComponent? woundable)
            || !TryComp(wound.HoldingWoundable, out BodyPartComponent? bodyPart)
            || !bodyPart.Body.HasValue)
            return;

        var result = _wound.TryHealBleedingWounds(wound.HoldingWoundable,
            (-delta * component.BleedingRemovalMultiplier).Float(),
            out var _,
            woundable);

        if (!result)
            return;

        _audio.PlayPvs(new SoundPathSpecifier("/Audio/Effects/lightburn.ogg"), bodyPart.Body.Value);
        _popup.PopupPredicted(Loc.GetString("bloodstream-component-wounds-cauterized"),
            bodyPart.Body.Value,
            bodyPart.Body.Value,
            PopupType.Medium);
    }

    // begin Goobstation: port EE height/width sliders
    public void SetBloodMaxVolume(Entity<BloodstreamComponent?> ent, FixedPoint2 volume)
    {
        if (!Resolve(ent.Owner, ref ent.Comp))
            return;

        ent.Comp.BloodReferenceSolution.Volume = (Content.Shared.FixedPoint.FixedPoint2)volume.Float();
    }
    // end Goobstation: port EE height/width sliders
}