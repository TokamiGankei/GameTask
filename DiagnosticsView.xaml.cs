using System.Windows;

namespace GameTaskPlugin
{
    public partial class DiagnosticsView : Window
    {
        public DiagnosticsView()
        {
            InitializeComponent();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
