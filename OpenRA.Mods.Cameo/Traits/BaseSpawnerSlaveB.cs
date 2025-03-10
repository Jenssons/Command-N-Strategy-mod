﻿#region Copyright & License Information
/*
 * Copyright 2015- OpenRA.Mods.AS Developers (see AUTHORS)
 * This file is a part of a third-party plugin for OpenRA, which is
 * free software. It is made available to you under the terms of the
 * GNU General Public License as published by the Free Software
 * Foundation. For more information, see COPYING.
 */
#endregion

using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.CA.Traits
{
	[Desc("Can be slaved to a SpawnerMaster.")]
	public class BaseSpawnerSlaveBInfo : ITraitInfo
	{
		[GrantedConditionReference]
		[Desc("The condition to grant to slaves when the master actor is killed.")]
		public readonly string MasterDeadCondition = null;

		[Desc("Can these actors be mind controlled or captured?")]
		public readonly bool AllowOwnerChange = false;

		[Desc("Types of damage this actor explodes with due to an unallowed slave action. Leave empty for no damage types.")]
		public readonly BitSet<DamageType> DamageTypes = default(BitSet<DamageType>);

		[GrantedConditionReference]
		[Desc("The condition to grant when the master trait is disabled.")]
		public readonly string GrantConditionWhenMasterIsDisabled = null;

		[GrantedConditionReference]
		[Desc("The condition to grant when the master trait is paused.")]
		public readonly string GrantConditionWhenMasterIsPaused = null;

		public virtual object Create(ActorInitializer init) { return new BaseSpawnerSlaveB(init, this); }
	}

	public class BaseSpawnerSlaveB : INotifyCreated, INotifyKilled, INotifyOwnerChanged
	{
		protected AttackBase[] attackBases;
		protected ConditionManager conditionManager;

		readonly BaseSpawnerSlaveBInfo info;

		public bool HasFreeWill = false;

		BaseSpawnerMasterB spawnerMaster = null;

		public Actor Master { get; private set; }

		// Make this actor attack a target.
		Target lastTarget;

		int masterDeadToken = ConditionManager.InvalidConditionToken;
		int masterTraitDisabledConditionToken = ConditionManager.InvalidConditionToken;
		int masterTraitPausedConditionToken = ConditionManager.InvalidConditionToken;

		public BaseSpawnerSlaveB(ActorInitializer init, BaseSpawnerSlaveBInfo info)
		{
			this.info = info;
		}

		void INotifyCreated.Created(Actor self)
		{
			Created(self);
		}

		protected virtual void Created(Actor self)
		{
			attackBases = self.TraitsImplementing<AttackBase>().ToArray();
			conditionManager = self.Trait<ConditionManager>();
		}

		void INotifyKilled.Killed(Actor self, AttackInfo e)
		{
			if (Master == null || Master.IsDead)
				return;

			spawnerMaster.OnSlaveKilled(Master, self);
		}

		public virtual void LinkMaster(Actor self, Actor master, BaseSpawnerMasterB spawnerMaster)
		{
			Master = master;
			this.spawnerMaster = spawnerMaster;
		}

		bool TargetSwitched(Target lastTarget, Target newTarget)
		{
			if (newTarget.Type != lastTarget.Type)
				return true;

			if (newTarget.Type == TargetType.Terrain)
				return newTarget.CenterPosition != lastTarget.CenterPosition;

			if (newTarget.Type == TargetType.Actor)
				return lastTarget.Actor != newTarget.Actor;

			return false;
		}

		// Stop what self was doing.
		public virtual void Stop(Actor self)
		{
			// Drop the target so that Attack() feels the need to assign target for this slave.
			lastTarget = Target.Invalid;

			self.CancelActivity();
		}

		public virtual void Attack(Actor self, Target target)
		{
			// Don't have to change target or alter current activity.
			if (!TargetSwitched(lastTarget, target))
				return;

			if (!target.IsValidFor(self))
			{
				Stop(self);
				return;
			}

			lastTarget = target;

			foreach (var ab in attackBases)
			{
				if (ab.IsTraitDisabled)
					continue;

				ab.AttackTarget(target, AttackSource.Default, false, true, true);
			}
		}

		public virtual void OnMasterKilled(Actor self, Actor attacker, SpawnerSlaveDisposal disposal)
		{
			// Grant MasterDead condition.
			// self.GrantCondition(info.MasterDeadCondition); // AS Style
			if (conditionManager != null && !string.IsNullOrEmpty(info.MasterDeadCondition))
				masterDeadToken = conditionManager.GrantCondition(self, info.MasterDeadCondition);

			switch (disposal)
			{
				case SpawnerSlaveDisposal.KillSlaves:
					if (attacker.IsDead)
						return;
					self.Kill(attacker, info.DamageTypes);
					break;
				case SpawnerSlaveDisposal.GiveSlavesToAttacker:
					self.CancelActivity();
					self.ChangeOwner(attacker.Owner);
					break;
				case SpawnerSlaveDisposal.DoNothing:
				// fall through
				default:
					break;
			}
		}

		// What if the master gets mind controlled?
		public virtual void OnMasterOwnerChanged(Actor self, Player oldOwner, Player newOwner, SpawnerSlaveDisposal disposal)
		{
			switch (disposal)
			{
				case SpawnerSlaveDisposal.KillSlaves:
					self.Kill(self, info.DamageTypes);
					break;
				case SpawnerSlaveDisposal.GiveSlavesToAttacker:
					self.CancelActivity();
					self.ChangeOwner(newOwner);
					break;
				case SpawnerSlaveDisposal.DoNothing:
				// fall through
				default:
					break;
			}
		}

		// What if the slave gets mind controlled?
		// Slaves aren't good without master so, kill it.
		void INotifyOwnerChanged.OnOwnerChanged(Actor self, Player oldOwner, Player newOwner)
		{
			// In this case, the slave will be disposed, one way or other.
			if (Master == null || !Master.IsDead)
				return;

			// This function got triggered because the master got mind controlled and
			// thus triggered slave.ChangeOwner().
			// In this case, do nothing.
			if (Master.Owner == newOwner)
				return;

			// These are independent, so why not let it be controlled?
			if (info.AllowOwnerChange)
				return;

			self.Kill(self, info.DamageTypes);
		}

		public void GrantMasterPausedCondition(Actor self)
		{
			if (masterTraitPausedConditionToken == ConditionManager.InvalidConditionToken)
				masterTraitPausedConditionToken = conditionManager.GrantCondition(self, info.GrantConditionWhenMasterIsPaused);
		}

		public void RevokeMasterPausedCondition(Actor self)
		{
			if (masterTraitPausedConditionToken != ConditionManager.InvalidConditionToken)
				masterTraitPausedConditionToken = conditionManager.RevokeCondition(self, masterTraitPausedConditionToken);
		}

		public void GrantMasterDisabledCondition(Actor self)
		{
			if (masterTraitDisabledConditionToken == ConditionManager.InvalidConditionToken)
				masterTraitDisabledConditionToken = conditionManager.GrantCondition(self, info.GrantConditionWhenMasterIsDisabled);
		}

		public void RevokeMasterDisabledCondition(Actor self)
		{
			if (masterTraitDisabledConditionToken != ConditionManager.InvalidConditionToken)
				masterTraitDisabledConditionToken = conditionManager.RevokeCondition(self, masterTraitDisabledConditionToken);
		}
	}
}
