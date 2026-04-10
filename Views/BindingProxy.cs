using System.Windows;

namespace NetImage.Views
{
    /// <summary>
    /// Allows bindings to reach a DataContext that is otherwise inaccessible from
    /// elements outside the visual tree (e.g. ContextMenu inside a Style Setter).
    /// Usage: declare as a resource with Data="{Binding}", then source menu commands
    /// with Source={StaticResource key}, Path=Data.SomeCommand.
    /// </summary>
    public class BindingProxy : Freezable
    {
        public static readonly DependencyProperty DataProperty =
            DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy));

        public object? Data
        {
            get => GetValue(DataProperty);
            set => SetValue(DataProperty, value);
        }

        protected override Freezable CreateInstanceCore() => new BindingProxy();
    }
}
