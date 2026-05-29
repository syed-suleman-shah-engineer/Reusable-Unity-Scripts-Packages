using System.Collections.Generic;
using UnityEngine;

namespace WitChess
{
    public class ChessUIController : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private Transform _boardParent;
        [SerializeField] private TileUI _tilePrefab;
        [SerializeField] private ChessUISettings _uiSettings;

        [Header("Game Settings")]
        [SerializeField] private EPlayer _humanPlayer = EPlayer.White;
        [SerializeField] private int _aiDepth = 4;
        [SerializeField] private bool _isAIEnabled = true;
        [SerializeField] private bool _rotateBoardOnTurnChange = false;
        [SerializeField] private bool _isMultiplayer = false;
        [SerializeField] private PromotionPopupUI _promotionPopup;

        private readonly TileUI[,] _tileUIs = new TileUI[8, 8];

        private ChessManager _chess;
        private MainPlayer _mainPlayer;
        private AIPlayer _aiPlayer;
        private LobbyPlayer _lobbyPlayer;

        private Spot _selectedSpot;
        private TileUI _lastMoveTileFrom;
        private TileUI _lastMoveTileTo;
        private Spot _checkHighlightSpot;
        private bool _isUndoingDouble;

        // Preview + queue state (active during AI's turn)
        private Spot _previewSelectedSpot;
        private readonly Dictionary<Spot, Move> _previewMoveCache = new();
        private Move _queuedMove;
        private Spot _queuedFromSpot;
        private Spot _queuedToSpot;

        // True when the local user controls the current turn:
        //   - Local 2-player (no AI, no network): always
        //   - vs AI or network multiplayer: only when it is the human player's colour
        private bool IsHumanTurn => (!_isAIEnabled && !_isMultiplayer) || _chess.CurrentPlayer == _humanPlayer;

        // ── Lifecycle ────────────────────────────────────────────────────────

        private void Awake()
        {
            if (_uiSettings == null) { Debug.LogError("ChessUISettings not assigned."); return; }
            GenerateLayout();
        }

        // ── Game Initialization ───────────────────────────────────────────────

        #region Public API

        public void StartGame(bool isAgainstAI, EPlayer humanPlaysAs, int aiSearchDepth, bool rotateBoardOnTurnChange, LobbyPlayer lobbyPlayer = null, bool isMultiplayer = false)
        {
            Board board = BoardFactory.CreateStandard();

            _chess = new ChessManager();
            _chess.OnMoveMade += HandleMoveMade;
            _chess.OnMoveUndone += HandleMoveUndone;
            _chess.OnTurnSwitched += HandleTurnSwitched;
            _chess.OnGameOver += HandleGameOver;
            _chess.OnCheck += HandleCheck;

            _isAIEnabled = isAgainstAI;
            _humanPlayer = humanPlaysAs;
            _rotateBoardOnTurnChange = rotateBoardOnTurnChange;
            _lobbyPlayer = lobbyPlayer;
            _isMultiplayer = isMultiplayer;
            _chess.Setup(board, EPlayer.White);

            _mainPlayer = new MainPlayer { PlayerType = _humanPlayer };
            _mainPlayer.OnMoveChosen += move => _chess.ExecuteMove(move);

            if (_isAIEnabled)
            {
                _aiDepth = aiSearchDepth;
                _aiPlayer = new AIPlayer(_chess.GameState) { PlayerType = _humanPlayer.Opponent(), Depth = _aiDepth };
                _aiPlayer.OnMoveChosen += move => _chess.ExecuteMove(move);
            }
            else if (_lobbyPlayer != null)
            {
                _lobbyPlayer.PlayerType = _humanPlayer.Opponent();
                _lobbyPlayer.OnMoveChosen += move => _chess.ExecuteMove(move);
            }

            RefreshAllPieces();
            UpdateBoardOrientationForCurrentTurn();
            NotifyCurrentPlayer();
        }

        // Undoes the last human move and the AI's response.
        // Only valid in AI mode when it is the human's turn (AI has already replied).
        public void Undo()
        {
            if (!_isAIEnabled) return;
            if (!IsHumanTurn) return;
            if (_chess.GameState.MoveCount < 2) return;

            // Suppress the mid-undo turn notification so the AI does not start
            // thinking again after the first undo temporarily restores its turn.
            _isUndoingDouble = true;
            _chess.UndoMove();      // revert AI's last move
            _isUndoingDouble = false;
            _chess.UndoMove();      // revert human's last move → human's turn restored
        }

        #endregion

        // ── Board Layout ──────────────────────────────────────────────────────

        private void GenerateLayout()
        {
            for (int row = 0; row < 8; row++)
            {
                for (int col = 0; col < 8; col++)
                {
                    TileUI tile = Instantiate(_tilePrefab, _boardParent);
                    tile.name = $"Tile_{row}_{col}";
                    _tileUIs[row, col] = tile;

                    bool isLight = (row + col) % 2 == 0;
                    tile.SetColor(isLight
                        ? _uiSettings.CurrentTemplateScheme.WhiteColorScheme.NormalTileColor
                        : _uiSettings.CurrentTemplateScheme.BlackColorScheme.NormalTileColor);

                    tile.SetHighlight(false, Color.clear);
                    tile.SetPieceSprite(null);

                    int r = row, c = col;
                    tile.OnTileClicked += _ => OnTileClicked(r, c);
                }
            }

            ApplyBoardRotation(_humanPlayer == EPlayer.White ? 180f : 0f);
        }

        // ── Input ─────────────────────────────────────────────────────────────

        private void OnTileClicked(int row, int col)
        {
            if (_chess.IsGameOver) return;

            Spot clicked = new Spot(row, col);

            if (IsHumanTurn)
            {
                // Human's turn — normal flow; wipe any leftover queue state
                ClearQueueHighlight();
                _queuedMove = null;

                if (_selectedSpot == null)
                    TrySelectPiece(clicked);
                else
                {
                    if (_chess.HasCachedMove(clicked, out Move move))
                    {
                        Spot fromSpot = _selectedSpot;
                        ClearHighlights();
                        _selectedSpot = null;

                        if (TryGetPromotionMoves(fromSpot, clicked, out List<PawnPromotion> promotions))
                        {
                            ShowPromotionPopup(promotions);
                            return;
                        }

                        _mainPlayer.OnMoveChosen?.Invoke(move);
                    }
                    else
                    {
                        ClearHighlights();
                        _selectedSpot = null;
                        TrySelectPiece(clicked);
                    }
                }
            }
            else
            {
                // AI's turn — allow preview and queueing one move
                if (_queuedMove != null)
                {
                    // Any click cancels the queued move
                    ClearQueueHighlight();
                    _queuedMove = null;
                    ClearPreviewHighlights();
                    _previewSelectedSpot = null;
                    _previewMoveCache.Clear();
                    TryPreviewSelect(clicked);
                }
                else if (_previewSelectedSpot == null)
                {
                    TryPreviewSelect(clicked);
                }
                else
                {
                    if (_previewMoveCache.TryGetValue(clicked, out Move move))
                    {
                        // Commit the queued move
                        ClearPreviewHighlights();
                        _previewSelectedSpot = null;
                        _queuedMove = move;
                        _queuedFromSpot = move.FromPos;
                        _queuedToSpot = move.ToPos;
                        ShowQueueHighlight();
                    }
                    else
                    {
                        // Clicked outside legal targets — re-select
                        ClearPreviewHighlights();
                        _previewSelectedSpot = null;
                        _previewMoveCache.Clear();
                        TryPreviewSelect(clicked);
                    }
                }
            }
        }

        private void TrySelectPiece(Spot spot)
        {
            if (!_chess.SelectPiece(spot)) return;
            _selectedSpot = spot;
            HighlightSelection(spot, _chess.GetCachedMoves());
        }

        private void TryPreviewSelect(Spot spot)
        {
            if (_chess.Board.IsEmpty(spot)) return;
            if (_chess.Board[spot].Player != _humanPlayer) return;

            _previewMoveCache.Clear();
            foreach (Move m in _chess.AllLegalMovesFor(_humanPlayer))
                if (m.FromPos == spot)
                    _previewMoveCache[m.ToPos] = m;

            if (_previewMoveCache.Count == 0) return;

            _previewSelectedSpot = spot;
            HighlightPreviewSelection(spot, _previewMoveCache);
        }

        // ── Event Handlers ────────────────────────────────────────────────────

        private void HandleMoveMade(Move move)
        {
            _lastMoveTileFrom?.SetHighlight(false, Color.clear);
            _lastMoveTileTo?.SetHighlight(false, Color.clear);
            ClearCheckHighlight();

            foreach (Move m in move.GetNormalMoves())
            {
                Piece movedPiece = _chess.Board[m.ToPos];
                Sprite moving = _tileUIs[m.FromPos.Row, m.FromPos.Column].GetPieceSprite();
                _tileUIs[m.ToPos.Row, m.ToPos.Column].SetPieceSprite(moving);
                _tileUIs[m.FromPos.Row, m.FromPos.Column].SetPieceSprite(null);

                if (m is PawnPromotion pp && movedPiece != null)
                    _tileUIs[m.ToPos.Row, m.ToPos.Column].SetPieceSprite(GetSprite(pp.NewType, movedPiece.Player));
            }

            // En passant: clear the captured pawn's square separately
            if (move is EnPassant ep)
                _tileUIs[ep.GetCapturedPawnPos().Row, ep.GetCapturedPawnPos().Column].SetPieceSprite(null);

            _lastMoveTileFrom = _tileUIs[move.FromPos.Row, move.FromPos.Column];
            _lastMoveTileTo = _tileUIs[move.ToPos.Row, move.ToPos.Column];
            bool fromLight = (move.FromPos.Row + move.FromPos.Column) % 2 == 0;
            bool toLight = (move.ToPos.Row + move.ToPos.Column) % 2 == 0;
            _lastMoveTileFrom.SetHighlight(true, fromLight
                ? _uiSettings.CurrentTemplateScheme.WhiteColorScheme.FromMoveHighlightColor
                : _uiSettings.CurrentTemplateScheme.BlackColorScheme.FromMoveHighlightColor);
            _lastMoveTileTo.SetHighlight(true, toLight
                ? _uiSettings.CurrentTemplateScheme.WhiteColorScheme.ToMoveHighlightColor
                : _uiSettings.CurrentTemplateScheme.BlackColorScheme.ToMoveHighlightColor);
        }

        private void HandleMoveUndone(Move _)
        {
            _lastMoveTileFrom?.SetHighlight(false, Color.clear);
            _lastMoveTileTo?.SetHighlight(false, Color.clear);
            _lastMoveTileFrom = null;
            _lastMoveTileTo = null;
            ClearCheckHighlight();
            RefreshAllPieces();
        }

        private void HandleTurnSwitched(EPlayer _)
        {
            UpdateBoardOrientationForCurrentTurn();
            NotifyCurrentPlayer();
        }

        private void HandleCheck(EPlayer _, Spot kingSpot)
        {
            ClearCheckHighlight();
            _checkHighlightSpot = kingSpot;
            _tileUIs[kingSpot.Row, kingSpot.Column].SetHighlight(true, _uiSettings.CurrentTemplateScheme.CheckHighlightColor);
        }

        private void HandleGameOver(Result result)
            => Debug.Log($"Game Over: {result}");

        // ── Turn Management ───────────────────────────────────────────────────

        private void NotifyCurrentPlayer()
        {
            if (_chess.IsGameOver) return;
            if (_isUndoingDouble) return;

            if (IsHumanTurn)
            {
                ClearQueueHighlight();

                if (_queuedMove != null)
                {
                    // Validate the queued move is still legal after the opponent's move
                    Move validated = null;
                    foreach (Move m in _chess.AllLegalMovesFor(_humanPlayer))
                    {
                        if (m.ToString() == _queuedMove.ToString()) { validated = m; break; }
                    }
                    _queuedMove = null;

                    if (validated != null)
                    {
                        _mainPlayer.OnMoveChosen.Invoke(validated);
                        return;
                    }
                }

                _mainPlayer.NotifyTurnToMove();
            }
            else
            {
                // Clear stale preview before opponent starts thinking
                ClearPreviewHighlights();
                _previewSelectedSpot = null;
                _previewMoveCache.Clear();
                _aiPlayer?.NotifyTurnToMove();
                _lobbyPlayer?.NotifyTurnToMove();
            }
        }

        private bool TryGetPromotionMoves(Spot from, Spot to, out List<PawnPromotion> promotions)
        {
            promotions = new List<PawnPromotion>();

            foreach (Move move in _chess.AllLegalMovesFor(_chess.CurrentPlayer))
            {
                if (move is PawnPromotion promotion && move.FromPos == from && move.ToPos == to)
                    promotions.Add(promotion);
            }

            return promotions.Count > 0;
        }

        private void ShowPromotionPopup(List<PawnPromotion> promotions)
        {
            PawnPromotion fallbackPromotion = promotions.Find(p => p.NewType == EPieceType.Queen) ?? promotions[0];

            if (_promotionPopup == null)
            {
                _mainPlayer.OnMoveChosen?.Invoke(fallbackPromotion);
                return;
            }

            _promotionPopup.Show(selectedType =>
            {
                PawnPromotion chosen = promotions.Find(p => p.NewType == selectedType) ?? fallbackPromotion;
                _mainPlayer.OnMoveChosen?.Invoke(chosen);
            });
        }

        private void UpdateBoardOrientationForCurrentTurn()
        {
            if (_boardParent == null || _chess == null) return;

            if (_rotateBoardOnTurnChange)
            {
                float turnAngle = _chess.CurrentPlayer == EPlayer.White ? 180f : 0f;
                ApplyBoardRotation(turnAngle);
                return;
            }

            float staticAngle = _humanPlayer == EPlayer.White ? 180f : 0f;
            ApplyBoardRotation(staticAngle);
        }

        private void ApplyBoardRotation(float boardZ)
        {
            _boardParent.localEulerAngles = new Vector3(0f, 0f, boardZ);

            for (int row = 0; row < 8; row++)
            {
                for (int col = 0; col < 8; col++)
                {
                    TileUI tile = _tileUIs[row, col];
                    if (tile != null)
                        tile.transform.localEulerAngles = new Vector3(0f, 0f, boardZ);
                }
            }
        }

        // ── Highlights ────────────────────────────────────────────────────────

        private void HighlightSelection(Spot from, IReadOnlyDictionary<Spot, Move> moves)
            => ApplyMoveHighlights(from, moves);

        private void HighlightPreviewSelection(Spot from, Dictionary<Spot, Move> moves)
            => ApplyMoveHighlights(from, moves);

        private void ApplyMoveHighlights(Spot from, IEnumerable<KeyValuePair<Spot, Move>> moves)
        {
            bool fromLight = (from.Row + from.Column) % 2 == 0;
            _tileUIs[from.Row, from.Column].SetHighlight(true, fromLight
                ? _uiSettings.CurrentTemplateScheme.WhiteColorScheme.HighlightedTileColor
                : _uiSettings.CurrentTemplateScheme.BlackColorScheme.HighlightedTileColor);

            foreach (var pair in moves)
            {
                Spot to = pair.Key;
                bool capture = !_chess.Board.IsEmpty(to);
                bool toLight = (to.Row + to.Column) % 2 == 0;
                ColorScheme scheme = toLight
                    ? _uiSettings.CurrentTemplateScheme.WhiteColorScheme
                    : _uiSettings.CurrentTemplateScheme.BlackColorScheme;
                _tileUIs[to.Row, to.Column].SetHighlight(true,
                    capture ? scheme.ToMoveHighlightColor : scheme.HighlightedTileColor);
            }
        }

        private void ClearHighlights()
        {
            if (_selectedSpot != null)
                _tileUIs[_selectedSpot.Row, _selectedSpot.Column].SetHighlight(false, Color.clear);
            foreach (var pair in _chess.GetCachedMoves())
                _tileUIs[pair.Key.Row, pair.Key.Column].SetHighlight(false, Color.clear);
            _chess.ClearSelection();
        }

        private void ClearPreviewHighlights()
        {
            if (_previewSelectedSpot != null)
                _tileUIs[_previewSelectedSpot.Row, _previewSelectedSpot.Column].SetHighlight(false, Color.clear);
            foreach (var pair in _previewMoveCache)
                _tileUIs[pair.Key.Row, pair.Key.Column].SetHighlight(false, Color.clear);
        }

        private void ShowQueueHighlight()
        {
            if (_queuedFromSpot == null || _queuedToSpot == null) return;
            bool fromLight = (_queuedFromSpot.Row + _queuedFromSpot.Column) % 2 == 0;
            bool toLight = (_queuedToSpot.Row + _queuedToSpot.Column) % 2 == 0;
            _tileUIs[_queuedFromSpot.Row, _queuedFromSpot.Column].SetHighlight(true, fromLight
                ? _uiSettings.CurrentTemplateScheme.WhiteColorScheme.FromMoveHighlightColor
                : _uiSettings.CurrentTemplateScheme.BlackColorScheme.FromMoveHighlightColor);
            _tileUIs[_queuedToSpot.Row, _queuedToSpot.Column].SetHighlight(true, toLight
                ? _uiSettings.CurrentTemplateScheme.WhiteColorScheme.ToMoveHighlightColor
                : _uiSettings.CurrentTemplateScheme.BlackColorScheme.ToMoveHighlightColor);
        }

        private void ClearQueueHighlight()
        {
            if (_queuedFromSpot != null)
                _tileUIs[_queuedFromSpot.Row, _queuedFromSpot.Column].SetHighlight(false, Color.clear);
            if (_queuedToSpot != null)
                _tileUIs[_queuedToSpot.Row, _queuedToSpot.Column].SetHighlight(false, Color.clear);
            _queuedFromSpot = null;
            _queuedToSpot = null;
        }

        private void ClearCheckHighlight()
        {
            if (_checkHighlightSpot == null) return;
            bool isLight = (_checkHighlightSpot.Row + _checkHighlightSpot.Column) % 2 == 0;
            _tileUIs[_checkHighlightSpot.Row, _checkHighlightSpot.Column].SetHighlight(false, Color.clear);
            _checkHighlightSpot = null;
        }

        // ── Visuals ───────────────────────────────────────────────────────────

        private void RefreshAllPieces()
        {
            for (int row = 0; row < 8; row++)
                for (int col = 0; col < 8; col++)
                {
                    Piece piece = _chess.Board[row, col];
                    _tileUIs[row, col].SetPieceSprite(piece != null ? GetSprite(piece.Type, piece.Player) : null);
                }
        }

        private Sprite GetSprite(EPieceType type, EPlayer player)
        {
            Skin skin = _uiSettings.CurrentSkin;
            if (skin == null) return null;
            return player == EPlayer.White
                ? type switch
                {
                    EPieceType.Pawn => skin.WhitePawn,
                    EPieceType.Knight => skin.WhiteKnight,
                    EPieceType.Bishop => skin.WhiteBishop,
                    EPieceType.Rook => skin.WhiteRook,
                    EPieceType.Queen => skin.WhiteQueen,
                    EPieceType.King => skin.WhiteKing,
                    _ => null
                }
                : type switch
                {
                    EPieceType.Pawn => skin.BlackPawn,
                    EPieceType.Knight => skin.BlackKnight,
                    EPieceType.Bishop => skin.BlackBishop,
                    EPieceType.Rook => skin.BlackRook,
                    EPieceType.Queen => skin.BlackQueen,
                    EPieceType.King => skin.BlackKing,
                    _ => null
                };
        }


#if UNITY_EDITOR

        [ContextMenu("Play Test Game vs AI")]
        private void PlayTestGameVsAI()
        {
            StartGame(isAgainstAI: true, humanPlaysAs: EPlayer.Black, aiSearchDepth: 2, rotateBoardOnTurnChange: false);
        }

        [ContextMenu("Play Test Game vs Lobby Player")]
        private void PlayTestGameVsLobbyPlayer()
        {
            LobbyPlayer lobbyPlayer = new LobbyPlayer();
            StartGame(isAgainstAI: false, humanPlaysAs: EPlayer.White, aiSearchDepth: 2, rotateBoardOnTurnChange: true, lobbyPlayer: lobbyPlayer, isMultiplayer: false);
        }

        [ContextMenu("Play Test Multiplayer Game")]
        private void PlayTestMultiplayerGame()
        {
            LobbyPlayer lobbyPlayer = new LobbyPlayer();
            StartGame(isAgainstAI: false, humanPlaysAs: EPlayer.White, aiSearchDepth: 2, rotateBoardOnTurnChange: false, lobbyPlayer: lobbyPlayer, isMultiplayer: true);
        }

        [ContextMenu("Test Multiplayer Random Move")]
        private void TestMultiplayerRandomMove()
        {
            if (_lobbyPlayer == null) return;

            List<Move> legalMoves = new List<Move>(_chess.AllLegalMovesFor(_lobbyPlayer.PlayerType));
            if (legalMoves.Count == 0) return;

            Move randomMove = legalMoves[Random.Range(0, legalMoves.Count)];
            _lobbyPlayer.OnMoveChosen?.Invoke(randomMove);
        }

        [ContextMenu("Undo Move")]
        private void UndoMove()
        {
            Undo();
        }

#endif
    }
}