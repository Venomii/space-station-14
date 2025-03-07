using System;
using System.Collections.Generic;
using Content.Shared.Administration.Logs;
using Content.Shared.Body.Prototypes;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Reaction;
using Content.Shared.Database;
using Content.Shared.FixedPoint;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom.Prototype;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom.Prototype.Dictionary;
using Robust.Shared.ViewVariables;

namespace Content.Shared.Chemistry.Reagent
{
    [Prototype("reagent")]
    [DataDefinition]
    public class ReagentPrototype : IPrototype, IInheritingPrototype
    {
        [ViewVariables]
        [DataField("id", required: true)]
        public string ID { get; } = default!;

        [DataField("name")]
        public string Name { get; } = string.Empty;

        [DataField("parent", customTypeSerializer:typeof(PrototypeIdSerializer<ReagentPrototype>))]
        public string? Parent { get; private set; }

        [NeverPushInheritance]
        [DataField("abstract")]
        public bool Abstract { get; private set; }

        [DataField("desc")]
        public string Description { get; } = string.Empty;

        [DataField("physicalDesc")]
        public string PhysicalDescription { get; } = string.Empty;

        [DataField("color")]
        public Color SubstanceColor { get; } = Color.White;

        [DataField("boilingPoint")]
        public float? BoilingPoint { get; }

        [DataField("meltingPoint")]
        public float? MeltingPoint { get; }

        [DataField("spritePath")]
        public string SpriteReplacementPath { get; } = string.Empty;

        [DataField("metabolisms", serverOnly: true, customTypeSerializer: typeof(PrototypeIdDictionarySerializer<ReagentEffectsEntry, MetabolismGroupPrototype>))]
        public Dictionary<string, ReagentEffectsEntry>? Metabolisms = null;

        [DataField("reactiveEffects", serverOnly: true, customTypeSerializer:typeof(PrototypeIdDictionarySerializer<ReactiveReagentEffectEntry, ReactiveGroupPrototype>))]
        public Dictionary<string, ReactiveReagentEffectEntry>? ReactiveEffects = null;

        [DataField("tileReactions", serverOnly: true)]
        public readonly List<ITileReaction> TileReactions = new(0);

        [DataField("plantMetabolism", serverOnly: true)]
        public readonly List<ReagentEffect> PlantMetabolisms = new(0);

        /// <summary>
        /// If the substance color is too dark we user a lighter version to make the text color readable when the user examines a solution.
        /// </summary>
        public Color GetSubstanceTextColor()
        {
            var highestValue = MathF.Max(SubstanceColor.R, MathF.Max(SubstanceColor.G, SubstanceColor.B));
            var difference = 0.5f - highestValue;

            if (difference > 0f)
            {
                return new Color(SubstanceColor.R + difference,
                                SubstanceColor.G + difference,
                                SubstanceColor.B + difference);
            }

            return SubstanceColor;
        }

        public FixedPoint2 ReactionTile(TileRef tile, FixedPoint2 reactVolume)
        {
            var removed = FixedPoint2.Zero;

            if (tile.Tile.IsEmpty)
                return removed;

            foreach (var reaction in TileReactions)
            {
                removed += reaction.TileReact(tile, this, reactVolume - removed);

                if (removed > reactVolume)
                    throw new Exception("Removed more than we have!");

                if (removed == reactVolume)
                    break;
            }

            return removed;
        }

        public void ReactionPlant(EntityUid? plantHolder, Solution.ReagentQuantity amount, Solution solution)
        {
            if (plantHolder == null)
                return;

            var entMan = IoCManager.Resolve<IEntityManager>();
            var random = IoCManager.Resolve<IRobustRandom>();
            var args = new ReagentEffectArgs(plantHolder.Value, null, solution, this, amount.Quantity, entMan, null);
            foreach (var plantMetabolizable in PlantMetabolisms)
            {
                if (!plantMetabolizable.ShouldApply(args, random))
                    continue;

                if (plantMetabolizable.ShouldLog)
                {
                    var entity = args.SolutionEntity;
                    EntitySystem.Get<SharedAdminLogSystem>().Add(LogType.ReagentEffect, plantMetabolizable.LogImpact,
                        $"Plant metabolism effect {plantMetabolizable.GetType().Name:effect} of reagent {ID:reagent} applied on entity {entMan.ToPrettyString(entity):entity} at {entMan.GetComponent<TransformComponent>(entity).Coordinates:coordinates}");
                    plantMetabolizable.Effect(args);
                }
            }
        }
    }

    [DataDefinition]
    public class ReagentEffectsEntry
    {
        /// <summary>
        ///     Amount of reagent to metabolize, per metabolism cycle.
        /// </summary>
        [DataField("metabolismRate")]
        public FixedPoint2 MetabolismRate = FixedPoint2.New(0.5f);

        /// <summary>
        ///     A list of effects to apply when these reagents are metabolized.
        /// </summary>
        [DataField("effects", required: true)]
        public ReagentEffect[] Effects = default!;
    }

    [DataDefinition]
    public class ReactiveReagentEffectEntry
    {
        [DataField("methods", required: true)]
        public HashSet<ReactionMethod> Methods = default!;

        [DataField("effects", required: true)]
        public ReagentEffect[] Effects = default!;
    }
}
