using System.Runtime.CompilerServices;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace LocalAutoPlay;

public partial class AutoPlayPanel : PanelContainer
{
    public const string NodeName = "LocalAutoPlayPanel";
    private const int MaxActions = 64;
    private Button? _button;
    private Button? _fullAutoButton;
    private Label? _status;
    private CancellationTokenSource? _stop;
    private bool _running;
    private bool _planning;
    private bool _playingAction;
    private bool _stopAfterCurrent;
    private string _lastStatus = "";

    public static void Attach(NCombatUi ui)
    {
        if (ui.GetNodeOrNull<AutoPlayPanel>(NodeName) is not null)
            return;
        var panel = new AutoPlayPanel
        {
            Name = NodeName,
            AnchorLeft = 0f,
            AnchorRight = 0f,
            AnchorTop = 0f,
            AnchorBottom = 0f,
            OffsetLeft = 18f,
            OffsetRight = 216f,
            OffsetTop = 170f,
            OffsetBottom = 329f,
            MouseFilter = MouseFilterEnum.Pass,
            ZIndex = 94
        };
        ui.AddChild(panel);
        MainFile.Log.Info("[LocalAutoPlay] combat panel attached.");
    }

    public override void _Ready()
    {
        AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.33f, 0.19f, 0.08f, 0.96f),
            BorderColor = new Color(0.08f, 0.035f, 0.012f),
            BorderWidthLeft = 5, BorderWidthRight = 5,
            BorderWidthTop = 5, BorderWidthBottom = 5,
            CornerRadiusTopLeft = 16, CornerRadiusTopRight = 11,
            CornerRadiusBottomLeft = 11, CornerRadiusBottomRight = 16,
            ContentMarginLeft = 9, ContentMarginRight = 9,
            ContentMarginTop = 7, ContentMarginBottom = 7
        });
        var stack = new VBoxContainer();
        stack.AddThemeConstantOverride("separation", 4);
        AddChild(stack);
        _button = MakeButton("▶ 自动一回合", new Color(0.59f, 0.35f, 0.13f));
        _button.Pressed += OnPressed;
        stack.AddChild(_button);
        _fullAutoButton = MakeButton("◇ 全自动：关", new Color(0.43f, 0.32f, 0.16f));
        _fullAutoButton.Pressed += OnFullAutoPressed;
        stack.AddChild(_fullAutoButton);
        _status = new Label
        {
            Text = "自动结束回合 · 不用药水",
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _status.AddThemeFontSizeOverride("font_size", 12);
        _status.AddThemeColorOverride("font_color", new Color(0.95f, 0.75f, 0.47f));
        stack.AddChild(_status);
        RefreshButtons();
        if (AutoPlaySettings.FullAutoEnabled) StartRunner();
    }

    private static Button MakeButton(string label, Color background)
    {
        var button = new Button
        {
            Text = label,
            CustomMinimumSize = new Vector2(166f, 48f),
            FocusMode = FocusModeEnum.None,
            MouseFilter = MouseFilterEnum.Stop
        };
        button.AddThemeFontSizeOverride("font_size", 18);
        button.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.52f));
        foreach (string state in new[] { "normal", "hover", "pressed" })
            button.AddThemeStyleboxOverride(state, new StyleBoxFlat
            {
                BgColor = state == "pressed" ? background.Darkened(0.25f) : background,
                BorderColor = new Color(0.12f, 0.05f, 0.015f),
                BorderWidthLeft = 4, BorderWidthRight = 4,
                BorderWidthTop = 4, BorderWidthBottom = 4,
                CornerRadiusTopLeft = 13, CornerRadiusTopRight = 9,
                CornerRadiusBottomLeft = 9, CornerRadiusBottomRight = 13
            });
        return button;
    }

    public override void _ExitTree()
    {
        _stop?.Cancel();
    }

    private void OnPressed()
    {
        if (AutoPlaySettings.FullAutoEnabled)
        {
            AutoPlaySettings.FullAutoEnabled = false;
            RefreshButtons();
        }
        if (_running) { RequestStop(); return; }
        StartRunner();
    }

    private void OnFullAutoPressed()
    {
        AutoPlaySettings.FullAutoEnabled = !AutoPlaySettings.FullAutoEnabled;
        RefreshButtons();
        if (!AutoPlaySettings.FullAutoEnabled)
        {
            if (_running) RequestStop();
            else SetStatus("全自动已关闭");
            return;
        }
        SetStatus("全自动已开启");
        if (!_running) StartRunner();
    }

    private void RequestStop()
    {
        if (_playingAction)
        {
            _stopAfterCurrent = true;
            SetStatus("当前牌结算后停止…");
        }
        else
        {
            _stop?.Cancel();
            SetStatus("正在停止…");
        }
    }

    private void RefreshButtons()
    {
        if (GodotObject.IsInstanceValid(_button))
            _button!.Text = _running ? "■ 停止本回合" : "▶ 自动一回合";
        if (GodotObject.IsInstanceValid(_fullAutoButton))
        {
            _fullAutoButton!.Text = AutoPlaySettings.FullAutoEnabled ? "◆ 全自动：开" : "◇ 全自动：关";
            _fullAutoButton.AddThemeColorOverride("font_color", AutoPlaySettings.FullAutoEnabled
                ? new Color(0.76f, 0.95f, 0.57f) : new Color(1f, 0.85f, 0.52f));
        }
    }

    private async void StartRunner()
    {
        if (_running || !IsInsideTree()) return;
        _running = true;
        _stopAfterCurrent = false;
        _stop = new CancellationTokenSource();
        RefreshButtons();
        try
        {
            int previousTurn = -1;
            while (true)
            {
                var ready = await WaitForTurn(previousTurn, _stop.Token);
                if (ready is null) break;
                (CombatState state, Player player, int turn) = ready.Value;
                bool finished = await PlayTurn(state, player, turn, _stop.Token);
                _stop.Token.ThrowIfCancellationRequested();
                if (!finished || _stopAfterCurrent || !CanAct(state, player, turn)) break;

                NEndTurnButton? endButton = NCombatRoom.Instance?.Ui?.EndTurnButton;
                if (endButton is null) { SetStatus("未找到结束回合按钮"); break; }
                endButton.CallReleaseLogic();
                MainFile.Log.Info($"[LocalAutoPlay] END_TURN turn={turn} fullAuto={AutoPlaySettings.FullAutoEnabled}");
                if (!await WaitForEndTurnAccepted(state, player, turn, _stop.Token))
                {
                    AutoPlaySettings.FullAutoEnabled = false;
                    SetStatus("自动结束回合未生效，请手动结束");
                    break;
                }
                if (!AutoPlaySettings.FullAutoEnabled)
                {
                    SetStatus("本回合已完成");
                    break;
                }
                previousTurn = turn;
                SetStatus("等待下一回合…");
            }
        }
        catch (OperationCanceledException) { SetStatus("已停止"); }
        catch (Exception ex)
        {
            AutoPlaySettings.FullAutoEnabled = false;
            SetStatus("异常，已暂停");
            MainFile.Log.Error($"[LocalAutoPlay] action failed: {ex}");
        }
        finally
        {
            _running = false;
            _planning = false;
            _playingAction = false;
            _stop?.Dispose();
            _stop = null;
            if (IsInsideTree()) RefreshButtons();
        }
    }

    private async Task<(CombatState State, Player Player, int Turn)?> WaitForTurn(
        int previousTurn, CancellationToken token)
    {
        while (IsInsideTree())
        {
            token.ThrowIfCancellationRequested();
            CombatState? state = CombatManager.Instance.DebugOnlyGetState();
            if (state is not null && CombatManager.Instance.IsInProgress)
            {
                if (state.Players.Count != 1)
                {
                    SetStatus("仅支持单人战斗");
                    return null;
                }
                Player? player = LocalContext.GetMe(state);
                if (player?.PlayerCombatState is { } pcs
                    && pcs.TurnNumber > previousTurn && CanAct(state, player, pcs.TurnNumber))
                    return (state, player, pcs.TurnNumber);
            }
            else if (previousTurn >= 0) return null;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        return null;
    }

    private async Task<bool> WaitUntilCanAct(CombatState state, Player player,
        int turn, CancellationToken token)
    {
        while (IsInsideTree())
        {
            token.ThrowIfCancellationRequested();
            if (!CombatManager.Instance.IsInProgress
                || !ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), state)
                || player.PlayerCombatState?.TurnNumber != turn
                || state.CurrentSide != CombatSide.Player)
                return false;
            // The hand can already be in Play mode while a queued card/orb
            // action is still resolving. Capturing then records temporarily
            // unplayable cards and can end the turn with a lethal card in hand.
            if (CanAct(state, player, turn)
                && !RunManager.Instance.ActionExecutor.IsRunning)
                return true;
            SetStatus("等待结算或界面关闭…");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        return false;
    }

    private async Task<bool> WaitForEndTurnAccepted(CombatState state, Player player,
        int turn, CancellationToken token)
    {
        for (int frame = 0; frame < 180 && IsInsideTree(); frame++)
        {
            token.ThrowIfCancellationRequested();
            if (!CombatManager.Instance.IsInProgress
                || !ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), state)
                || state.CurrentSide != CombatSide.Player
                || player.PlayerCombatState?.TurnNumber != turn
                || CombatManager.Instance.IsPlayerReadyToEndTurn(player)
                || CombatManager.Instance.PlayerActionsDisabled)
                return true;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        return false;
    }

    private async Task<bool> PlayTurn(CombatState state, Player player, int turn,
        CancellationToken token)
    {
        int failedAttempts = 0;
        int emptyPlanRetries = 0;
        int stalePlanRetries = 0;
        for (int i = 0; i < MaxActions; i++)
        {
            if (!await WaitUntilCanAct(state, player, turn, token)) return false;
            string plannedFrom = CombatSignature(state, player);
            _planning = true;
            SetStatus("正在预测本回合…");
            LocalMove? move;
            try { move = await TurnForecastPlanner.ChooseAsync(state, player, token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                MainFile.Log.Warn($"[LocalAutoPlay] forecast unavailable; using fallback: {ex}");
                move = LocalPlanner.Choose(state, player);
            }
            finally { _planning = false; }
            token.ThrowIfCancellationRequested();
            if (!await WaitUntilCanAct(state, player, turn, token)) return false;
            if (CombatSignature(state, player) != plannedFrom)
            {
                if (++stalePlanRetries > 4)
                {
                    SetStatus("局面持续变化，已暂停自动出牌");
                    return false;
                }
                i--;
                MainFile.Log.Info("[LocalAutoPlay] RETRY_PLAN stale combat snapshot");
                continue;
            }

            if (move is null)
            {
                if (emptyPlanRetries < 2
                    && HasPotentiallyTransientCard(player)
                    && await WaitForHandChange(state, player, turn, token))
                {
                    emptyPlanRetries++;
                    i--; // A wait is not a played card.
                    MainFile.Log.Info("[LocalAutoPlay] RETRY_PLAN after hand/playability change");
                    continue;
                }
                MainFile.Log.Info($"[LocalAutoPlay] NO_MOVE turn={turn} " +
                    $"energy={player.PlayerCombatState?.Energy} " +
                    $"hand={DescribeHand(player)} orbs={HandSignature(player)}");
                SetStatus($"已打 {i} 张，准备结束回合");
                return true;
            }
            SetStatus($"出牌 {i + 1}/{MaxActions}");
            _playingAction = true;
            try { await PlayOne(move.Value, token); }
            catch (InvalidOperationException ex)
            {
                if (++failedAttempts >= 3) throw;
                MainFile.Log.Warn($"[LocalAutoPlay] stale move, replanning: {ex.Message}");
                continue;
            }
            finally { _playingAction = false; }
            failedAttempts = 0;
            emptyPlanRetries = 0;
            stalePlanRetries = 0;
            MainFile.Log.Info($"[LocalAutoPlay] PLAY turn={turn} index={i + 1} " +
                $"card={move.Value.Card.Id.Entry} score={move.Value.Score:0.0}");
            if (_stopAfterCurrent) { SetStatus("已停止"); return false; }
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        SetStatus("已达单回合出牌上限，准备结束回合");
        return true;
    }

    private static bool HasPotentiallyTransientCard(Player player)
    {
        foreach (CardModel card in player.PlayerCombatState?.Hand.Cards ?? [])
        {
            try
            {
                if (card.CanPlay(out UnplayableReason reason, out _)) continue;
                if ((reason & (UnplayableReason.BlockedByHook
                    | UnplayableReason.BlockedByCardLogic)) != 0
                    && (reason & (UnplayableReason.HasUnplayableKeyword
                        | UnplayableReason.EnergyCostTooHigh
                        | UnplayableReason.StarCostTooHigh)) == 0)
                    return true;
            }
            catch { /* A card mid-transition is a reason to wait, not end. */
                return true; }
        }
        return false;
    }

    private async Task<bool> WaitForHandChange(CombatState state, Player player,
        int turn, CancellationToken token)
    {
        string initial = HandSignature(player);
        if (!HasPotentiallyTransientCard(player)) return true;
        SetStatus("等待卡牌结算…");
        for (int frame = 0; frame < 36 && IsInsideTree(); frame++)
        {
            token.ThrowIfCancellationRequested();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!CombatManager.Instance.IsInProgress
                || !ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), state)
                || player.PlayerCombatState?.TurnNumber != turn
                || state.CurrentSide != CombatSide.Player)
                return false;
            if (!RunManager.Instance.ActionExecutor.IsRunning
                && (HandSignature(player) != initial
                    || !HasPotentiallyTransientCard(player)))
                return true;
        }
        return false;
    }

    private static string HandSignature(Player player)
    {
        PlayerCombatState? pcs = player.PlayerCombatState;
        if (pcs is null) return "no-hand";
        string cards = string.Join(',', pcs.Hand.Cards.Select(card =>
        {
            bool playable;
            try { playable = card.CanPlay(); }
            catch { playable = false; }
            var enchant = card.Enchantment;
            return $"{RuntimeHelpers.GetHashCode(card)}/{playable}/" +
                $"{card.EnergyCost.GetAmountToSpend()}/" +
                $"{enchant?.Id.Entry}/{enchant?.Status}/{enchant?.Amount}";
        }));
        string orbs = string.Join(',', pcs.OrbQueue.Orbs.Select(orb =>
            $"{orb.GetType().Name}/{orb.EvokeVal}"));
        return $"{pcs.Energy}:{cards}:{orbs}";
    }

    private static string CombatSignature(CombatState state, Player player) =>
        HandSignature(player) + ":" + PowerSignature(player.Creature) + ":" +
        string.Join(',', player.Relics.Select(r =>
            $"{r.Id}/{r.DisplayAmount}")) + ":" +
        string.Join(',', state.HittableEnemies.Select(e =>
            $"{RuntimeHelpers.GetHashCode(e)}/{e.CurrentHp}/{e.Block}/" +
            PowerSignature(e)));

    private static string PowerSignature(Creature creature) =>
        string.Join(',', creature.Powers.Select(p => $"{p.Id}/{p.Amount}")
            .OrderBy(s => s, StringComparer.Ordinal));

    private static string DescribeHand(Player player)
    {
        return string.Join(',', player.PlayerCombatState?.Hand.Cards.Select(card =>
        {
            try
            {
                bool playable = card.CanPlay(out UnplayableReason reason, out _);
                return $"{card.Id.Entry}/{card.EnergyCost.GetAmountToSpend()}/" +
                    (playable ? "ready" : reason.ToString());
            }
            catch (Exception ex) { return $"{card.Id.Entry}/{ex.GetType().Name}"; }
        }) ?? []);
    }

    private static bool CanAct(CombatState state, Player player, int turn)
    {
        try
        {
            return CombatManager.Instance.IsInProgress
                && ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), state)
                && !CombatManager.Instance.PlayerActionsDisabled
                && state.CurrentSide == CombatSide.Player
                && player.PlayerCombatState?.TurnNumber == turn
                && player.PlayerCombatState.Phase == PlayerTurnPhase.Play
                && NCombatRoom.Instance?.Ui?.Hand?.CurrentMode == NPlayerHand.Mode.Play;
        }
        catch { return false; }
    }

    private static async Task PlayOne(LocalMove move, CancellationToken token)
    {
        if (CardSelectCmd.Selector is not null)
            throw new InvalidOperationException("Another card selector is already active.");
        var source = new TaskCompletionSource<GameAction>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = RunManager.Instance.ActionExecutor;
        void Capture(GameAction action)
        {
            if (action is PlayCardAction play
                && ReferenceEquals(play.NetCombatCard.ToCardModelOrNull(), move.Card))
                source.TrySetResult(action);
        }
        executor.BeforeActionExecuted += Capture;
        try
        {
            using IDisposable selector = CardSelectCmd.PushSelector(
                new AutoCardSelector(move.Card, move.SelectedCards));
            if (!move.Card.TryManualPlay(move.Target))
                throw new InvalidOperationException($"{move.Card.Id.Entry} became unplayable.");
            GameAction action = await source.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            await action.CompletionTask.WaitAsync(TimeSpan.FromSeconds(45), token);
        }
        finally { executor.BeforeActionExecuted -= Capture; }
    }

    private void SetStatus(string value)
    {
        if (_lastStatus == value) return;
        _lastStatus = value;
        if (GodotObject.IsInstanceValid(_status))
            _status!.Text = value;
        if (!value.StartsWith("出牌 ", StringComparison.Ordinal))
            MainFile.Log.Info($"[LocalAutoPlay] STATUS {value}");
    }
}
