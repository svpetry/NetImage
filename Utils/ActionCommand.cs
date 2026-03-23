using System.Windows.Input;

namespace NetImage.Utils
{
    public class ActionCommand : ICommand
    {
        private readonly Action<object?> _executeHandler;
        private bool _enabled = true;

        public ActionCommand(Action<object?> execute)
        {
            if (execute == null)
                throw new ArgumentNullException(nameof(execute));
            _executeHandler = execute;
        }

        public bool Enabled
        {
            get
            {
                return _enabled;
            }
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                CanExecuteChanged?.Invoke(this, new EventArgs());
            }
        }

        public void Execute(object? parameter)
        {
            _executeHandler(parameter);
        }

        public bool CanExecute(object? parameter)
        {
            return _enabled;
        }

        public event EventHandler? CanExecuteChanged;
    }
}