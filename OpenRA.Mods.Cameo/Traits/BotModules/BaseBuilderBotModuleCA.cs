﻿#region Copyright & License Information
/*
 * Copyright 2007-2020 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CA.Traits
{
	[Desc("Manages AI base construction.")]
	public class BaseBuilderBotModuleCAInfo : ConditionalTraitInfo
	{
		[Desc("Tells the AI what building types are considered construction yards.")]
		public readonly HashSet<string> ConstructionYardTypes = new HashSet<string>();

		[Desc("Tells the AI what building types are considered vehicle production facilities.")]
		public readonly HashSet<string> VehiclesFactoryTypes = new HashSet<string>();

		[Desc("Tells the AI what building types are considered refineries.")]
		public readonly HashSet<string> RefineryTypes = new HashSet<string>();

		[Desc("Tells the AI what building types are considered power plants.")]
		public readonly HashSet<string> PowerTypes = new HashSet<string>();

		[Desc("Tells the AI what building types are considered infantry production facilities.")]
		public readonly HashSet<string> BarracksTypes = new HashSet<string>();

		[Desc("Tells the AI what building types are considered anti-air defenses.")]
		public readonly HashSet<string> AntiAirTypes = new HashSet<string>();

		[Desc("Tells the AI what building types are considered production facilities.")]
		public readonly HashSet<string> ProductionTypes = new HashSet<string>();

		[Desc("Tells the AI what building types are considered naval production facilities.")]
		public readonly HashSet<string> NavalProductionTypes = new HashSet<string>();

		[Desc("Tells the AI what building types are considered silos (resource storage).")]
		public readonly HashSet<string> SiloTypes = new HashSet<string>();

		[Desc("Production queues AI uses for buildings.")]
		public readonly HashSet<string> BuildingQueues = new HashSet<string> { "Building" };

		[Desc("Production queues AI uses for defenses.")]
		public readonly HashSet<string> DefenseQueues = new HashSet<string> { "Defense" };

		[Desc("Minimum distance in cells from center of the base when checking for building placement.")]
		public readonly int MinBaseRadius = 2;

		[Desc("Radius in cells around the center of the base to expand.")]
		public readonly int MaxBaseRadius = 20;

		[Desc("Maximum number of refineries to build.")]
		public readonly int MaxRefineries = 10;

		[Desc("Minimum excess power the AI should try to maintain.")]
		public readonly int MinimumExcessPower = 0;

		[Desc("The targeted excess power the AI tries to maintain cannot rise above this.")]
		public readonly int MaximumExcessPower = 0;

		[Desc("Increase maintained excess power by this amount for every ExcessPowerIncreaseThreshold of base buildings.")]
		public readonly int ExcessPowerIncrement = 0;

		[Desc("Increase maintained excess power by ExcessPowerIncrement for every N base buildings.")]
		public readonly int ExcessPowerIncreaseThreshold = 1;

		[Desc("Additional delay (in ticks) between structure production checks when there is no active production.",
			"StructureProductionRandomBonusDelay is added to this.")]
		public readonly int StructureProductionInactiveDelay = 125;

		[Desc("Additional delay (in ticks) added between structure production checks when actively building things.",
			"Note: The total delay is gamespeed OrderLatency x 4 + this + StructureProductionRandomBonusDelay.")]
		public readonly int StructureProductionActiveDelay = 0;

		[Desc("A random delay (in ticks) of up to this is added to active/inactive production delays.")]
		public readonly int StructureProductionRandomBonusDelay = 10;

		[Desc("Delay (in ticks) until retrying to build structure after the last 3 consecutive attempts failed.")]
		public readonly int StructureProductionResumeDelay = 1500;

		[Desc("After how many failed attempts to place a structure should AI give up and wait",
			"for StructureProductionResumeDelay before retrying.")]
		public readonly int MaximumFailedPlacementAttempts = 3;

		[Desc("How many randomly chosen cells with resources to check when deciding refinery placement.")]
		public readonly int MaxResourceCellsToCheck = 3;

		[Desc("Delay (in ticks) until rechecking for new BaseProviders.")]
		public readonly int CheckForNewBasesDelay = 1500;

		[Desc("Chance that the AI will place the defenses in the direction of the closest enemy building.")]
		public readonly int PlaceDefenseTowardsEnemyChance = 100;

		[Desc("Minimum range at which to build defensive structures near a combat hotspot.")]
		public readonly int MinimumDefenseRadius = 5;

		[Desc("Maximum range at which to build defensive structures near a combat hotspot.")]
		public readonly int MaximumDefenseRadius = 20;

		[Desc("Try to build another production building if there is too much cash.")]
		public readonly int NewProductionCashThreshold = 5000;

		[Desc("Radius in cells around a factory scanned for rally points by the AI.")]
		public readonly int RallyPointScanRadius = 8;

		[Desc("Radius in cells around each building with ProvideBuildableArea",
			"to check for a 3x3 area of water where naval structures can be built.",
			"Should match maximum adjacency of naval structures.")]
		public readonly int CheckForWaterRadius = 8;

		[Desc("Terrain types which are considered water for base building purposes.")]
		public readonly HashSet<string> WaterTerrainTypes = new HashSet<string> { "Water" };

		[Desc("What buildings to the AI should build.", "What integer percentage of the total base must be this type of building.")]
		public readonly Dictionary<string, int> BuildingFractions = null;

		[Desc("What buildings should the AI have a maximum limit to build.")]
		public readonly Dictionary<string, int> BuildingLimits = null;

		[Desc("When should the AI start building specific buildings.")]
		public readonly Dictionary<string, int> BuildingDelays = null;

		public override object Create(ActorInitializer init) { return new BaseBuilderBotModuleCA(init.Self, this); }
	}

	public class BaseBuilderBotModuleCA : ConditionalTrait<BaseBuilderBotModuleCAInfo>, IGameSaveTraitData,
		IBotTick, IBotPositionsUpdated, IBotRespondToAttack, IBotRequestPauseUnitProduction
	{
		public CPos GetRandomBaseCenter()
		{
			var randomConstructionYard = world.Actors.Where(a => a.Owner == player &&
				Info.ConstructionYardTypes.Contains(a.Info.Name))
				.RandomOrDefault(world.LocalRandom);

			return randomConstructionYard != null ? randomConstructionYard.Location : initialBaseCenter;
		}

		public CPos DefenseCenter { get { return defenseCenter; } }

		readonly World world;
		readonly Player player;
		PowerManager playerPower;
		PlayerResources playerResources;
		IBotPositionsUpdated[] positionsUpdatedModules;
		BitArray resourceTypeIndices;
		CPos initialBaseCenter;
		CPos defenseCenter;

		List<BaseBuilderQueueManagerCA> builders = new List<BaseBuilderQueueManagerCA>();

		public BaseBuilderBotModuleCA(Actor self, BaseBuilderBotModuleCAInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;
		}

		protected override void Created(Actor self)
		{
			// Special case handling is required for the Player actor.
			// Created is called before Player.PlayerActor is assigned,
			// so we must query player traits from self, which refers
			// for bot modules always to the Player actor.
			playerPower = self.TraitOrDefault<PowerManager>();
			playerResources = self.Trait<PlayerResources>();
			positionsUpdatedModules = self.TraitsImplementing<IBotPositionsUpdated>().ToArray();
		}

		protected override void TraitEnabled(Actor self)
		{
			var tileset = world.Map.Rules.TileSet;
			resourceTypeIndices = new BitArray(tileset.TerrainInfo.Length); // Big enough
			foreach (var t in world.Map.Rules.Actors["world"].TraitInfos<ResourceTypeInfo>())
				resourceTypeIndices.Set(tileset.GetTerrainIndex(t.TerrainType), true);

			foreach (var building in Info.BuildingQueues)
				builders.Add(new BaseBuilderQueueManagerCA(this, building, player, playerPower, playerResources, resourceTypeIndices));
			foreach (var defense in Info.DefenseQueues)
				builders.Add(new BaseBuilderQueueManagerCA(this, defense, player, playerPower, playerResources, resourceTypeIndices));
		}

		void IBotPositionsUpdated.UpdatedBaseCenter(CPos newLocation)
		{
			initialBaseCenter = newLocation;
		}

		void IBotPositionsUpdated.UpdatedDefenseCenter(CPos newLocation)
		{
			defenseCenter = newLocation;
		}

		bool IBotRequestPauseUnitProduction.PauseUnitProduction
		{
			get { return !IsTraitDisabled && !HasAdequateRefineryCount; }
		}

		void IBotTick.BotTick(IBot bot)
		{
			SetRallyPointsForNewProductionBuildings(bot);

			foreach (var b in builders)
				b.Tick(bot);
		}

		void IBotRespondToAttack.RespondToAttack(IBot bot, Actor self, AttackInfo e)
		{
			if (e.Attacker == null || e.Attacker.Disposed)
				return;

			if (e.Attacker.Owner.Stances[self.Owner] != Stance.Enemy)
				return;

			if (!e.Attacker.Info.HasTraitInfo<ITargetableInfo>())
				return;

			// Protect buildings
			if (self.Info.HasTraitInfo<BuildingInfo>())
				foreach (var n in positionsUpdatedModules)
					n.UpdatedDefenseCenter(e.Attacker.Location);
		}

		void SetRallyPointsForNewProductionBuildings(IBot bot)
		{
			foreach (var rp in world.ActorsWithTrait<RallyPoint>())
			{
				if (rp.Actor.Owner != player)
					continue;

				if (rp.Trait.Path.Count == 0 || !IsRallyPointValid(rp.Trait.Path[0], rp.Actor.Info.TraitInfoOrDefault<BuildingInfo>()))
				{
					bot.QueueOrder(new Order("SetRallyPoint", rp.Actor, Target.FromCell(world, ChooseRallyLocationNear(rp.Actor)), false)
					{
						SuppressVisualFeedback = true
					});
				}
			}
		}

		// Won't work for shipyards...
		CPos ChooseRallyLocationNear(Actor producer)
		{
			var possibleRallyPoints = world.Map.FindTilesInCircle(producer.Location, Info.RallyPointScanRadius)
				.Where(c => IsRallyPointValid(c, producer.Info.TraitInfoOrDefault<BuildingInfo>()));

			if (!possibleRallyPoints.Any())
			{
				AIUtils.BotDebug("Bot Bug: No possible rallypoint near {0}", producer.Location);
				return producer.Location;
			}

			return possibleRallyPoints.Random(world.LocalRandom);
		}

		bool IsRallyPointValid(CPos x, BuildingInfo info)
		{
			return info != null && world.IsCellBuildable(x, null, info);
		}

		public bool HasMaxRefineries
		{
			get
			{
				return AIUtils.CountBuildingByCommonName(Info.RefineryTypes, player) >= Info.MaxRefineries;
			}
		}

		public bool HasAdequateRefineryCount
		{
			get
			{
				// Require at least one refinery, unless we can't build it.
				return AIUtils.CountBuildingByCommonName(Info.RefineryTypes, player) >= 1 ||
					AIUtils.CountBuildingByCommonName(Info.PowerTypes, player) == 0 ||
					AIUtils.CountBuildingByCommonName(Info.ConstructionYardTypes, player) == 0;
			}
		}

		public bool HasAdequateBarracksCount
		{
			get
			{
				// Require at least one barracks, unless we can't build it.
				return AIUtils.CountBuildingByCommonName(Info.BarracksTypes, player) >= 1 ||
					AIUtils.CountBuildingByCommonName(Info.PowerTypes, player) == 0 ||
					AIUtils.CountBuildingByCommonName(Info.RefineryTypes, player) == 0 ||
					AIUtils.CountBuildingByCommonName(Info.ConstructionYardTypes, player) == 0;
			}
		}

		public bool HasAdequateFactoryCount
		{
			get
			{
				// Require at least one factory, unless we can't build it.
				return AIUtils.CountBuildingByCommonName(Info.VehiclesFactoryTypes, player) >= 1 ||
					AIUtils.CountBuildingByCommonName(Info.PowerTypes, player) == 0 ||
					AIUtils.CountBuildingByCommonName(Info.RefineryTypes, player) == 0 ||
					AIUtils.CountBuildingByCommonName(Info.ConstructionYardTypes, player) == 0;
			}
		}

		List<MiniYamlNode> IGameSaveTraitData.IssueTraitData(Actor self)
		{
			if (IsTraitDisabled)
				return null;

			return new List<MiniYamlNode>()
			{
				new MiniYamlNode("InitialBaseCenter", FieldSaver.FormatValue(initialBaseCenter)),
				new MiniYamlNode("DefenseCenter", FieldSaver.FormatValue(defenseCenter))
			};
		}

		void IGameSaveTraitData.ResolveTraitData(Actor self, List<MiniYamlNode> data)
		{
			if (self.World.IsReplay)
				return;

			var initialBaseCenterNode = data.FirstOrDefault(n => n.Key == "InitialBaseCenter");
			if (initialBaseCenterNode != null)
				initialBaseCenter = FieldLoader.GetValue<CPos>("InitialBaseCenter", initialBaseCenterNode.Value.Value);

			var defenseCenterNode = data.FirstOrDefault(n => n.Key == "DefenseCenter");
			if (defenseCenterNode != null)
				defenseCenter = FieldLoader.GetValue<CPos>("DefenseCenter", defenseCenterNode.Value.Value);
		}
	}
}
