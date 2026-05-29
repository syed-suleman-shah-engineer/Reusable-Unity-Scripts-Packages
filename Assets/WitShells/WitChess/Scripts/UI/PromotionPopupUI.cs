using UnityEngine;
using UnityEngine.Events;

namespace WitChess
{
    public class PromotionPopupUI : MonoBehaviour
    {
        [System.Serializable]
        public class PieceTypeEvent : UnityEvent<EPieceType> { }

        [Header("References")]
        [SerializeField] private GameObject _popupRoot;

        [Header("Events")]
        public PieceTypeEvent OnPieceTypeSelected;

        public UnityAction<EPieceType> OnPieceTypeSelectedAction;

        private UnityAction<EPieceType> _pendingSelection;

        private void Awake()
        {
            Hide();
        }

        public void Show(UnityAction<EPieceType> onSelected)
        {
            _pendingSelection = onSelected;
            if (_popupRoot != null)
                _popupRoot.SetActive(true);
            else
                gameObject.SetActive(true);
        }

        public void Hide()
        {
            if (_popupRoot != null)
                _popupRoot.SetActive(false);
            else
                gameObject.SetActive(false);
        }

        public void SelectQueen() => SelectPieceType(EPieceType.Queen);

        public void SelectRook() => SelectPieceType(EPieceType.Rook);

        public void SelectBishop() => SelectPieceType(EPieceType.Bishop);

        public void SelectKnight() => SelectPieceType(EPieceType.Knight);

        public void SelectPieceType(EPieceType selectedType)
        {
            Hide();

            _pendingSelection?.Invoke(selectedType);
            OnPieceTypeSelectedAction?.Invoke(selectedType);
            OnPieceTypeSelected?.Invoke(selectedType);

            _pendingSelection = null;
        }
    }
}
