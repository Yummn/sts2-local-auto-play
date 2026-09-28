using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace LocalAutoPlay;

public partial class AutoPlayPanel : PanelContainer
{
    public const string NodeName = "LocalAutoPlayPanel";
    private const int MaxActions = 24;
    private Button? _button;
    private Label? _status;
    private CancellationTokenSource? _stop;
    private bool _running;
    private bool _stopAfterCurrent;

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
            OffsetBottom = 270f,
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
        _button = new Button
        {
            Text = "▶ 自动打牌",
            CustomMinimumSize = new Vector2(166f, 51f),
            FocusMode = FocusModeEnum.None,
            MouseFilter = MouseFilterEnum.Stop
        };
        _button.AddThemeFontSizeOverride("font_size", 19);
        _button.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.52f));
        foreach (string state in new[] { "normal", "hover", "pressed" })
            _button.AddThemeStyleboxOverride(state, new StyleBoxFlat
            {
                BgColor = state == "pressed" ? new Color(0.35f, 0.19f, 0.07f)
                    : new Color(0.59f, 0.35f, 0.13f),
                BorderColor = new Color(0.12f, 0.05f, 0.015f),
                BorderWidthLeft = 4, BorderWidthRight = 4,
                BorderWidthTop = 4, BorderWidthBottom = 4,
                CornerRadiusTopLeft = 13, CornerRadiusTopRight = 9,
                CornerRadiusBottomLeft = 9, CornerRadiusBottomRight = 13
            });
        _button.Pressed += OnPressed;
        stack.AddChild(_button);
        _status = new Label
        {
            Text = "仅当前回合 · 不用药水",
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _status.AddThemeFontSizeOverride("font_size", 12);
        _status.AddThemeColorOverride("font_color", new Color(0.95f, 0.75f, 0.47f));
        stack.AddChild(_status);
    }

    public override void _ExitTree()
    {
        _stop?.Cancel();
        _stop?.Dispose();
        _stop = null;
    }

    private async void OnPressed()
    {
        if (_running)
        {
            _stopAfterCurrent = true;
            SetStatus("当前牌结算后停止…");
            return;
        }
        _running = true;
        _stopAfterCurrent = false;
        _stop = new CancellationTokenSource();
        _button!.Text = "■ 停止出牌";
        try { await PlayTurn(_stop.Token); }
        catch (OperationCanceledException) { SetStatus("已停止"); }
        catch (Exception ex)
        {
            SetStatus("异常，已暂停");
            MainFile.Log.Error($"[LocalAutoPlay] action failed: {ex}");
        }
        finally
        {
            _running = false;
            if (GodotObject.IsInstanceValid(_button))
                _button!.Text = "▶ 自动打牌";
            _stop?.Dispose();
            _stop = null;
        }
    }

    private async Task PlayTurn(CancellationToken token)
    {
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        Player? player = state is null ? null : LocalContext.GetMe(state);
        if (state is null || player?.PlayerCombatState is null || state.Players.Count != 1)
        {
            SetStatus("仅支持单人战斗");
            return;
        }
        int turn = player.PlayerCombatState.TurnNumber;
        for (int i = 0; i < MaxActions; i++)
        {
            token.ThrowIfCancellationRequested();
            if (!CanAct(state, player, turn))
            {
                SetStatus("回合或界面已变化");
                return;
            }
            LocalMove? move = LocalPlanner.Choose(state, player);
            if (move is null)
            {
                SetStatus(i == 0 ? "没有适合自动打出的牌" : $"已打 {i} 张，剩余手动");
                return;
            }
            SetStatus($"出牌 {i + 1}/{MaxActions}");
            await PlayOne(move.Value, token);
            MainFile.Log.Info($"[LocalAutoPlay] PLAY turn={turn} index={i + 1} card={move.Value.Card.Id.Entry} score={move.Value.Score:0.0}");
            if (_stopAfterCurrent)
            {
                SetStatus("已停止");
                return;
            }
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        SetStatus("已达单次出牌上限");
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
            using IDisposable selector = CardSelectCmd.PushSelector(new AutoCardSelector(move.Card));
            if (!move.Card.TryManualPlay(move.Target))
                throw new InvalidOperationException($"{move.Card.Id.Entry} became unplayable.");
            GameAction action = await source.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            await action.CompletionTask.WaitAsync(TimeSpan.FromSeconds(25), token);
        }
        finally { executor.BeforeActionExecuted -= Capture; }
    }

    private void SetStatus(string value)
    {
        if (GodotObject.IsInstanceValid(_status))
            _status!.Text = value;
        if (!value.StartsWith("出牌 ", StringComparison.Ordinal))
            MainFile.Log.Info($"[LocalAutoPlay] STOP status={value}");
    }
}
