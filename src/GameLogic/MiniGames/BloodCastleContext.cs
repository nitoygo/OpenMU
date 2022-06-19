﻿// <copyright file="BloodCastleContext.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.MiniGames;

using System.Collections.Concurrent;
using System.Threading;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.GameLogic.Views.Character;
using MUnique.OpenMU.GameLogic.Views.Inventory;
using MUnique.OpenMU.Pathfinding;
using Nito.Disposables.Internals;

/// <summary>
/// The context of a blood castle game.
/// </summary>
/// <remarks>
/// A blood castle event works like that:
///   First, a player or his party (and probably another party), maximum 10 players, enter the blood castle.
///   The game has several states:
///   * After the event starts, first a certain amount of monsters have to be killed, so that a bridge appears on the way to the castle gate.
///   * The castle gate has to be destroyed
///   * Some stronger monsters wait after the castle gate. A certain amount of "Spirit Sorcerer" have to be killed.
///   * The statue appears, which needs to be killed.
///   * The statue drops an archangel weapon as quest item. This item has to be brought back to the archangel NPC.
/// The game has a time limit of usually 15 or 20 minutes.
/// After the time is up, or the quest item has been brought back, the players get some rewards:
/// 
/// Experience:
///   1. For each remaining second, a bonus experience is given as experience.
///   2. The player or party which destroyed the gate, gets extra experience. Dead party members get the half of the exp as bonus.
///   3. For killing the statue, the player/party gets a bonus experience.
///   4. For finishing the quest, the player/party gets a bonus experience.
///   5. To all previous exp rewards, bonuses from seals, maps etc. are applied.
/// Money:
///   According to the reward table - fixed values per blood castle level, depending if the player was in the winning party, or not.
///   The winner/winners party get roughly the double money value.
/// Score:
///   a) If the event was won by any participant, depending on the individual success state of a player,
///      it will get a different score rewarded. A player is categorized into these 5 states:
///      - unfinished event
///      - died during event
///      - winner
///      - member of winners party
///      - member of winners party, but died during event.
///   b) If the event wasn't won by any participant, players are getting a score penalty of 300.
/// </remarks>
public sealed class BloodCastleContext : MiniGameContext
{
    private const short CastleGateNumber = 131;
    private const short StatueOfSaintNumber = 132;
    private const short RequiredKillsBeforeBridgePerPlayer = 10;
    private const short RequiredKillsAfterGatePerPlayer = 2;

    private static readonly Point StatusOfSaintSpawnPoint = new (14, 95);

    /// <summary>
    /// A set of monster ids which should count as kill after the gate has been destroyed.
    /// These are all for "Spirit Sorcerer" monsters, for the different levels of blood castle.
    /// </summary>
    private static readonly HashSet<short> CountableMonstersAfterGate = new () { 89, 95, 112, 118, 124, 130, 143, 433 };

    private readonly ItemDefinition _questItemDefinition;
    private readonly MonsterDefinition _statueDefinition;
    private readonly ConcurrentDictionary<string, PlayerGameState> _gameStates = new ();

    private IReadOnlyCollection<(string Name, int Score, int BonusExp, int BonusMoney)>? _highScoreTable;

    private int _requiredMonsterKills;
    private int _currentMonsterKills;
    private TimeSpan _remainingTime;

    private bool _gateDestroyed;
    private bool _statueSpawned;
    private bool _bridgeToggled;

    private Player? _winner;
    private Player? _questItemOwner;
    private Item? _questItem;

    /// <summary>
    /// Initializes a new instance of the <see cref="BloodCastleContext"/> class.
    /// </summary>
    /// <param name="key">The key of this context.</param>
    /// <param name="definition">The definition of the mini game.</param>
    /// <param name="gameContext">The game context, to which this game belongs.</param>
    /// <param name="mapInitializer">The map initializer, which is used when the event starts.</param>
    public BloodCastleContext(MiniGameMapKey key, MiniGameDefinition definition, IGameContext gameContext, IMapInitializer mapInitializer)
        : base(key, definition, gameContext, mapInitializer)
    {
        this._questItemDefinition = gameContext.Configuration.Items.FirstOrDefault(def => def.IsArchangelQuestItem())
                                    ?? throw new InvalidOperationException("The required quest item is not defined in the game configuration.");
        this._statueDefinition = gameContext.Configuration.Monsters.FirstOrDefault(m => m.Number == StatueOfSaintNumber)
                                 ?? throw new InvalidOperationException($"The required statue definition (Number {StatueOfSaintNumber}) is not defined in the game configuration.");
    }

    /// <inheritdoc />
    protected override Player? Winner => this._winner;

    /// <inheritdoc />
    protected override TimeSpan RemainingTime => this._remainingTime;

    private int PlayerCount => this._gameStates.Count;

    /// <summary>
    /// Player interact with Archangel.
    /// </summary>
    /// <param name="player">The player who talks to Archangel.</param>
    /// <remarks>
    /// TODO: Replace magic values (category/dialog numbers) with constants or enums.
    /// </remarks>
    public void TalkToNpcArchangel(Player player)
    {
        if (this._winner is not null)
        {
            player.ViewPlugIns.GetPlugIn<IShowDialogPlugIn>()?.ShowDialog(1, 0x2E);
            return;
        }

        if (!this.IsEventRunning)
        {
            player.ViewPlugIns.GetPlugIn<IShowDialogPlugIn>()?.ShowDialog(1, 0x18);
            return;
        }

        if (!this.TryRemoveQuestItemFromPlayer(player))
        {
            player.ViewPlugIns.GetPlugIn<IShowDialogPlugIn>()?.ShowDialog(1, 0x18);
            return;
        }

        this._winner = player;
        player.ViewPlugIns.GetPlugIn<IShowDialogPlugIn>()?.ShowDialog(1, 0x17);
        this.FinishEvent();
    }

    /// <inheritdoc/>
    protected override void OnObjectRemovedFromMap(object? sender, (GameMap Map, ILocateable Object) args)
    {
        if (args.Object is Player player)
        {
            if (this.IsEventRunning)
            {
                // Drop it, so that the remaining players can pick it up.
                this.TryDropQuestItemFromPlayer(player);
            }
            else
            {
                this.TryRemoveQuestItemFromPlayer(player);
            }

            this.UpdateState(BloodCastleStatus.Ended, player);
        }

        base.OnObjectRemovedFromMap(sender, args);
    }

    /// <inheritdoc />
    protected override void OnDestructibleDied(object? sender, DeathInformation e)
    {
        base.OnDestructibleDied(sender, e);
        var destructible = sender as Destructible;
        if (destructible is null)
        {
            return;
        }

        switch (destructible.Definition.Number)
        {
            case CastleGateNumber:
                this.OnCastleGateKilled(e);
                break;

            case StatueOfSaintNumber:
            {
                this.OnStatueKilled(e, destructible);
                break;
            }

            default:
                throw new NotImplementedException($"Unknown destructible was killed: {destructible}");
        }
    }

    /// <inheritdoc />
    protected override void OnMonsterDied(object? sender, DeathInformation e)
    {
        base.OnMonsterDied(sender, e);
        if (this._gameStates.TryGetValue(e.KillerName, out var state))
        {
            state.AddScore(this.Definition.GameLevel);
        }

        var monster = sender as Monster;
        if (monster is null)
        {
            return;
        }

        if (!this._bridgeToggled)
        {
            if (this._currentMonsterKills < this._requiredMonsterKills)
            {
                this._currentMonsterKills++;
            }

            if (this._currentMonsterKills >= this._requiredMonsterKills)
            {
                this.BridgeToggle(true);
            }

            return;
        }

        if (!this._statueSpawned && this._gateDestroyed)
        {
            if (this._currentMonsterKills < this._requiredMonsterKills && CountableMonstersAfterGate.Contains(monster.Definition.Number))
            {
                this._currentMonsterKills++;
            }

            if (this._currentMonsterKills >= this._requiredMonsterKills)
            {
                this.SpawnStatue();
                this._statueSpawned = true;

                _ = Task.Run(() => this.ForEachPlayerAsync(player =>
                {
                    player.ViewPlugIns.GetPlugIn<IShowMessagePlugIn>()?
                        .ShowMessage("Kundun minions have been subdued! Destroy the Crystal Statue!", Interfaces.MessageType.GoldenCenter);
                }));
            }
        }
    }

    /// <inheritdoc/>
    protected override void OnPlayerPickedUpItem(object? sender, ILocateable e)
    {
        var picker = sender as Player;

        if (e is DroppedItem { Item.Definition: { } definition } && definition.IsArchangelQuestItem())
        {
            this._questItemOwner = picker;
            _ = Task.Run(() => this.ForEachPlayerAsync(player =>
            {
                player.ViewPlugIns.GetPlugIn<IShowMessagePlugIn>()?
                    .ShowMessage(picker!.Name + " has acquired the " + definition.Name, Interfaces.MessageType.GoldenCenter);
            }));
        }
    }

    /// <inheritdoc />
    protected override void OnGameStart(ICollection<Player> players)
    {
        foreach (var player in players)
        {
            this._gameStates.TryAdd(player.Name, new PlayerGameState(player));
        }

        this._requiredMonsterKills = this.PlayerCount * RequiredKillsBeforeBridgePerPlayer;
        this.EntranceToggle(true);

        _ = Task.Run(async () => await this.ShowRemainingTimeLoopAsync(this.GameEndedToken), this.GameEndedToken);
        base.OnGameStart(players);
    }

    /// <inheritdoc />
    protected override void GameEnded(ICollection<Player> finishers)
    {
        this.UpdateState(BloodCastleStatus.Ended);

        var sortedFinishers = finishers
            .Select(f => this._gameStates[f.Name])
            .WhereNotNull()
            .OrderBy(state => state.Score)
            .ToList();

        var scoreList = new List<(string Name, int Score, int BonusExp, int BonusMoney)>();
        int rank = 0;
        foreach (var state in sortedFinishers)
        {
            rank++;
            state.Rank = rank;
            var (bonusScore, givenMoney) = this.GiveRewardsAndGetBonusScore(state.Player, rank);
            state.AddScore(bonusScore);

            scoreList.Add((
                state.Player.Name,
                state.Score,
                this.Definition.Rewards.FirstOrDefault(r => r.RewardType == MiniGameRewardType.Experience && (r.Rank is null || r.Rank == rank))?.RewardAmount ?? 0,
                givenMoney));

            this.TryRemoveQuestItemFromPlayer(state.Player);
        }

        this._highScoreTable = scoreList.AsReadOnly();

        this.SaveRanking(sortedFinishers.Select(state => (state.Rank, state.Player.SelectedCharacter!, state.Score)));
        base.GameEnded(finishers);
    }

    /// <inheritdoc />
    protected override void ShowScore(Player player)
    {
        if (this._highScoreTable is { } table)
        {
            var isSuccessful = this._winner is not null;
            var (name, score, bonusMoney, bonusExp) = table.First(t => t.Name == player.Name);
            player.ViewPlugIns.GetPlugIn<IBloodCastleScoreTableViewPlugin>()?.ShowScoreTable(isSuccessful, name, score, bonusExp, bonusMoney);
        }
    }

    private void OnStatueKilled(DeathInformation e, Destructible destructible)
    {
        var item = new TemporaryItem
        {
            Definition = this._questItemDefinition,
        };

        var dropper = this._gameStates.FirstOrDefault(s => s.Key == e.KillerName).Value.Player;
        var dropped = new DroppedItem(item, destructible.Position, this.Map, dropper);
        this.Map.Add(dropped);

        this._questItem = item;

        this.ForEachPlayerAsync(player =>
        {
            player.ViewPlugIns.GetPlugIn<IShowMessagePlugIn>()?
                .ShowMessage(e.KillerName + " has destroyed the stone Statue!", Interfaces.MessageType.GoldenCenter);
        }).ConfigureAwait(false);
    }

    private void OnCastleGateKilled(DeathInformation e)
    {
        this._gateDestroyed = true;
        this._currentMonsterKills = 0;
        this._requiredMonsterKills = this.PlayerCount * RequiredKillsAfterGatePerPlayer;
        this.GateToggle(true);

        this.ForEachPlayerAsync(player =>
        {
            player.ViewPlugIns.GetPlugIn<IShowMessagePlugIn>()?
                .ShowMessage(e.KillerName + " has demolished the Castle Gate!", Interfaces.MessageType.GoldenCenter);
        }).ConfigureAwait(false);
    }

    private void EntranceToggle(bool value)
    {
        var areas = new List<(byte startX, byte startY, byte endX, byte endY)>
        {
            (13, 15, 15, 23),
            (11, 78, 25, 89),
            (08, 78, 10, 83),
        };

        this.UpdateWalkMapClient(areas, TerrainAttributeType.Blocked, value);
        this.UpdateWalkMapServer(areas, value);
    }

    private void BridgeToggle(bool value)
    {
        this._bridgeToggled = value;

        var areas = new List<(byte startX, byte startY, byte endX, byte endY)>
        {
            (13, 70, 15, 75),
        };

        this.UpdateWalkMapClient(areas, TerrainAttributeType.NoGround, value);
        this.UpdateWalkMapServer(areas, value);
    }

    private void GateToggle(bool value)
    {
        var areas = new List<(byte startX, byte startY, byte endX, byte endY)>
        {
            (13, 76, 15, 79),
        };

        this.UpdateWalkMapClient(areas, TerrainAttributeType.Blocked, value);
        this.UpdateWalkMapServer(areas, value);
    }

    private void SpawnStatue()
    {
        var area = new MonsterSpawnArea
        {
            GameMap = this.Map.Definition,
            MonsterDefinition = this._statueDefinition,
            SpawnTrigger = SpawnTrigger.OnceAtWaveStart,
            Direction = Direction.SouthWest,
            Quantity = 1,
            X1 = StatusOfSaintSpawnPoint.X,
            X2 = StatusOfSaintSpawnPoint.X,
            Y1 = StatusOfSaintSpawnPoint.Y,
            Y2 = StatusOfSaintSpawnPoint.Y,
        };

        var statue = new Destructible(area, this._statueDefinition, this.Map);
        statue.Initialize();
        this.Map.Add(statue);
    }

    private void UpdateWalkMapServer(
        List<(byte startX, byte startY, byte endX, byte endY)> areas,
        bool value)
    {
        foreach (var (startX, startY, endX, endY) in areas)
        {
            for (int x = startX; x <= endX; x++)
            {
                for (int y = startY; y <= endY; y++)
                {
                    this.Map.Terrain.WalkMap[x, y] = value;
                }
            }
        }
    }

    private void UpdateWalkMapClient(
        List<(byte startX, byte startY, byte endX, byte endY)> areas,
        TerrainAttributeType type,
        bool value)
    {
        Task.Run(() => this.ForEachPlayerAsync(player =>
        {
            player.ViewPlugIns.GetPlugIn<IChangeTerrainAttributesViewPlugin>()?
                .ChangeAttributes(false, type, value, areas);
        }));
    }

    private async ValueTask ShowRemainingTimeLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            var timerInterval = TimeSpan.FromSeconds(1);
            using var timer = new PeriodicTimer(timerInterval);
            var maximumGameDuration = this.Definition.GameDuration;
            this._remainingTime = maximumGameDuration;

            this.UpdateState(BloodCastleStatus.Started);
            while (!cancellationToken.IsCancellationRequested
                   && this._remainingTime >= TimeSpan.Zero
                   && await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (this._remainingTime < maximumGameDuration && !this._gateDestroyed)
                {
                    this.UpdateState(BloodCastleStatus.GateNotDestroyed);
                }

                if (this._remainingTime < maximumGameDuration && this._gateDestroyed)
                {
                    this.UpdateState(BloodCastleStatus.GateDestroyed);
                }

                this._remainingTime = this._remainingTime.Subtract(timerInterval);
            }
        }
        catch (TaskCanceledException)
        {
        }
        catch (Exception ex)
        {
            this.Logger.LogError(ex, "Unexpected error during update blood castle status: {0}", ex.Message);
        }
    }

    private void UpdateState(BloodCastleStatus status)
    {
        _ = Task.Run(() => this.ForEachPlayerAsync(player => this.UpdateState(status, player)));
    }

    private void UpdateState(BloodCastleStatus status, Player player)
    {
        player.ViewPlugIns.GetPlugIn<IBloodCastleStateViewPlugin>()?
            .UpdateState(
                status,
                this._remainingTime,
                this._requiredMonsterKills,
                this._currentMonsterKills,
                this._questItemOwner,
                this._questItem);
    }

    private bool TryRemoveQuestItemFromPlayer(Player player)
    {
        if (!player.TryGetQuestItem(out var item))
        {
            return false;
        }

        player.Inventory!.RemoveItem(item);
        player.PersistenceContext.Delete(item);
        player.ViewPlugIns.GetPlugIn<IItemRemovedPlugIn>()?.RemoveItem(item.ItemSlot);

        this._questItem = null;
        this._questItemOwner = null;

        return true;
    }

    private bool TryDropQuestItemFromPlayer(Player player)
    {
        if (!player.TryGetQuestItem(out var item))
        {
            return false;
        }

        var dropped = new DroppedItem(item, player.Position, this.Map, player);
        this.Map.Add(dropped);
        player.Inventory!.RemoveItem(item);
        player.ViewPlugIns.GetPlugIn<IItemDropResultPlugIn>()?.ItemDropResult(item.ItemSlot, true);

        this._questItem = item;
        this._questItemOwner = null;

        return true;
    }

    private sealed class PlayerGameState
    {
        private int _score;

        public PlayerGameState(Player player)
        {
            if (player.SelectedCharacter?.CharacterClass is null)
            {
                throw new InvalidOperationException($"The player '{player}' is in the wrong state");
            }

            this.Player = player;
        }

        public Player Player { get; }

        public int Score => this._score;

        public int Rank { get; set; }

        public void AddScore(int value)
        {
            Interlocked.Add(ref this._score, value);
        }
    }
}