using System.Collections.Generic;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Items;
using Content.Server.Light.Components;
using Content.Shared.Audio;
using Content.Shared.Interaction;
using Content.Shared.Smoking;
using Content.Shared.Temperature;
using Robust.Shared.Audio;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Player;

namespace Content.Server.Light.EntitySystems
{
    public class MatchstickSystem : EntitySystem
    {
        private HashSet<MatchstickComponent> _litMatches = new();
        [Dependency]
        private readonly AtmosphereSystem _atmosphereSystem = default!;

        public override void Initialize()
        {
            base.Initialize();
            SubscribeLocalEvent<MatchstickComponent, InteractUsingEvent>(OnInteractUsing);
            SubscribeLocalEvent<MatchstickComponent, IsHotEvent>(OnIsHotEvent);
            SubscribeLocalEvent<MatchstickComponent, ComponentShutdown>(OnShutdown);
        }

        private void OnShutdown(EntityUid uid, MatchstickComponent component, ComponentShutdown args)
        {
            _litMatches.Remove(component);
        }

        public override void Update(float frameTime)
        {
            base.Update(frameTime);
            foreach (var match in _litMatches)
            {
                if (match.CurrentState != SmokableState.Lit || match.Paused || match.Deleted)
                    continue;

                _atmosphereSystem.HotspotExpose(EntityManager.GetComponent<TransformComponent>(match.Owner).Coordinates, 400, 50, true);
            }
        }

        private void OnInteractUsing(EntityUid uid, MatchstickComponent component, InteractUsingEvent args)
        {
            if (args.Handled || component.CurrentState != SmokableState.Unlit)
                return;

            var isHotEvent = new IsHotEvent();
            RaiseLocalEvent(args.Used, isHotEvent, false);

            if (!isHotEvent.IsHot)
                return;

            Ignite(component, args.User);
            args.Handled = true;
        }

        private void OnIsHotEvent(EntityUid uid, MatchstickComponent component, IsHotEvent args)
        {
            args.IsHot = component.CurrentState == SmokableState.Lit;
        }

        public void Ignite(MatchstickComponent component, EntityUid user)
        {
            // Play Sound
            SoundSystem.Play(
                Filter.Pvs(component.Owner), component.IgniteSound.GetSound(), component.Owner,
                AudioHelpers.WithVariation(0.125f).WithVolume(-0.125f));

            // Change state
            SetState(component, SmokableState.Lit);
            _litMatches.Add(component);
            component.Owner.SpawnTimer(component.Duration * 1000, delegate
            {
                SetState(component, SmokableState.Burnt);
                _litMatches.Remove(component);
            });
        }

        private void SetState(MatchstickComponent component, SmokableState value)
        {
            component.CurrentState = value;

            if (component.PointLightComponent != null)
            {
                component.PointLightComponent.Enabled = component.CurrentState == SmokableState.Lit;
            }

            if (EntityManager.TryGetComponent(component.Owner, out ItemComponent? item))
            {
                switch (component.CurrentState)
                {
                    case SmokableState.Lit:
                        item.EquippedPrefix = "lit";
                        break;
                    default:
                        item.EquippedPrefix = "unlit";
                        break;
                }
            }

            if (EntityManager.TryGetComponent(component.Owner, out AppearanceComponent? appearance))
            {
                appearance.SetData(SmokingVisuals.Smoking, component.CurrentState);
            }
        }
    }
}
