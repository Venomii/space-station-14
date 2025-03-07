using System.Collections.Generic;
using Content.Server.Storage.Components;
using Content.Shared.Body.Components;
using Content.Shared.Directions;
using Content.Shared.Examine;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Helpers;
using Content.Shared.Morgue;
using Content.Shared.Physics;
using Content.Shared.Popups;
using Content.Shared.Sound;
using Content.Shared.Standing;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Localization;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Player;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Utility;
using Robust.Shared.Utility.Markup;
using Robust.Shared.ViewVariables;

namespace Content.Server.Morgue.Components
{
    [RegisterComponent]
    [ComponentReference(typeof(EntityStorageComponent))]
    [ComponentReference(typeof(IActivate))]
    [ComponentReference(typeof(IStorageComponent))]
#pragma warning disable 618
    public class MorgueEntityStorageComponent : EntityStorageComponent, IExamine
#pragma warning restore 618
    {
        [Dependency] private readonly IEntityManager _entMan = default!;

        public override string Name => "MorgueEntityStorage";

        [ViewVariables(VVAccess.ReadWrite)]
        [DataField("trayPrototype")]
        private string? _trayPrototypeId;

        [ViewVariables]
        private EntityUid? _tray;

        [ViewVariables]
        public ContainerSlot? TrayContainer { get; private set; }

        [ViewVariables(VVAccess.ReadWrite)]
        [DataField("doSoulBeep")]
        public bool DoSoulBeep = true;

        [DataField("occupantHasSoulAlarmSound")]
        private SoundSpecifier _occupantHasSoulAlarmSound = new SoundPathSpecifier("/Audio/Weapons/Guns/EmptyAlarm/smg_empty_alarm.ogg");

        [ViewVariables]
        [ComponentDependency] protected readonly AppearanceComponent? Appearance = null;

        protected override void Initialize()
        {
            base.Initialize();
            Appearance?.SetData(MorgueVisuals.Open, false);
            TrayContainer = Owner.EnsureContainer<ContainerSlot>("morgue_tray", out _);
        }

        public override Vector2 ContentsDumpPosition()
        {
            if (_tray != null)
                return _entMan.GetComponent<TransformComponent>(_tray.Value).WorldPosition;
            return base.ContentsDumpPosition();
        }

        protected override bool AddToContents(EntityUid entity)
        {
            if (_entMan.HasComponent<SharedBodyComponent>(entity) && !EntitySystem.Get<StandingStateSystem>().IsDown(entity))
                return false;
            return base.AddToContents(entity);
        }

        public override bool CanOpen(EntityUid user, bool silent = false)
        {
            if (!Owner.InRangeUnobstructed(
                _entMan.GetComponent<TransformComponent>(Owner).Coordinates.Offset(_entMan.GetComponent<TransformComponent>(Owner).LocalRotation.GetCardinalDir()),
                collisionMask: CollisionGroup.Impassable | CollisionGroup.VaultImpassable
            ))
            {
                if (!silent)
                    Owner.PopupMessage(user, Loc.GetString("morgue-entity-storage-component-cannot-open-no-space"));
                return false;
            }

            return base.CanOpen(user, silent);
        }

        protected override void OpenStorage()
        {
            Appearance?.SetData(MorgueVisuals.Open, true);
            Appearance?.SetData(MorgueVisuals.HasContents, false);
            Appearance?.SetData(MorgueVisuals.HasMob, false);
            Appearance?.SetData(MorgueVisuals.HasSoul, false);

            if (_tray == null)
            {
                _tray = _entMan.SpawnEntity(_trayPrototypeId, _entMan.GetComponent<TransformComponent>(Owner).Coordinates);
                var trayComp = _tray.Value.EnsureComponent<MorgueTrayComponent>();
                trayComp.Morgue = Owner;
            }
            else
            {
                TrayContainer?.Remove(_tray.Value);
            }

            _entMan.GetComponent<TransformComponent>(_tray.Value).Coordinates = new EntityCoordinates(Owner, 0, -1);

            base.OpenStorage();
        }

        private void CheckContents()
        {
            var count = 0;
            var hasMob = false;
            var hasSoul = false;
            foreach (var entity in Contents.ContainedEntities)
            {
                count++;
                if (!hasMob && _entMan.HasComponent<SharedBodyComponent>(entity))
                    hasMob = true;
                if (!hasSoul && _entMan.TryGetComponent<ActorComponent?>(entity, out var actor) && actor.PlayerSession != null)
                    hasSoul = true;
            }
            Appearance?.SetData(MorgueVisuals.HasContents, count > 0);
            Appearance?.SetData(MorgueVisuals.HasMob, hasMob);
            Appearance?.SetData(MorgueVisuals.HasSoul, hasSoul);
        }

        protected override void CloseStorage()
        {
            base.CloseStorage();

            Appearance?.SetData(MorgueVisuals.Open, false);
            CheckContents();

            if (_tray != null)
            {
                TrayContainer?.Insert(_tray.Value);
            }
        }

        protected override IEnumerable<EntityUid> DetermineCollidingEntities()
        {
            if (_tray == null)
            {
                yield break;
            }

            var entityLookup = IoCManager.Resolve<IEntityLookup>();
            foreach (var entity in entityLookup.GetEntitiesIntersecting(_tray.Value, flags: LookupFlags.None))
            {
                yield return entity;
            }
        }

        //Called every 10 seconds
        public void Update()
        {
            CheckContents();

            if (DoSoulBeep && Appearance != null && Appearance.TryGetData(MorgueVisuals.HasSoul, out bool hasSoul) && hasSoul)
            {
                SoundSystem.Play(Filter.Pvs(Owner), _occupantHasSoulAlarmSound.GetSound(), Owner);
            }
        }

        void IExamine.Examine(FormattedMessage.Builder message, bool inDetailsRange)
        {
            if (Appearance == null) return;

            if (inDetailsRange)
            {
                if (Appearance.TryGetData(MorgueVisuals.HasSoul, out bool hasSoul) && hasSoul)
                {
                    message.AddMarkup(Loc.GetString("morgue-entity-storage-component-on-examine-details-body-has-soul"));
                }
                else if (Appearance.TryGetData(MorgueVisuals.HasMob, out bool hasMob) && hasMob)
                {
                    message.AddMarkup(Loc.GetString("morgue-entity-storage-component-on-examine-details-body-has-no-soul"));
                }
                else if (Appearance.TryGetData(MorgueVisuals.HasContents, out bool hasContents) && hasContents)
                {
                    message.AddMarkup(Loc.GetString("morgue-entity-storage-component-on-examine-details-has-contents"));
                }
                else
                {
                    message.AddMarkup(Loc.GetString("morgue-entity-storage-component-on-examine-details-empty"));
                }
            }
        }
    }
}
