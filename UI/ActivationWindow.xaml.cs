using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RevenantWorkspaceWarden.Providers;

namespace RevenantWorkspaceWarden.UI
{
    public partial class ActivationWindow : Window
    {
        // Set this to your Stripe Payment Link once the product is live.
        private const string StoreUrl = "https://revenantsystems.net/warden-pro";

        public ActivationWindow()
        {
            InitializeComponent();
            LicenseKeyBox.Focus();
        }

        private void ActivateBtn_Click(object sender, RoutedEventArgs e)
        {
            string key = LicenseKeyBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                ShowError("Enter your license key.");
                return;
            }

            bool valid = LicenseManager.ValidateAndActivate(key);

            if (valid)
            {
                StatusText.Foreground = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#3ad5a1"));
                StatusText.Text    = "Workspace Warden Pro activated.";
                ActivateBtn.Content = "Done";
                ActivateBtn.IsEnabled = false;

                Dispatcher.InvokeAsync(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(800);
                    DialogResult = true;
                    Close();
                });
            }
            else
            {
                ShowError("Invalid license key. Check the key and try again.");
            }
        }

        private void LicenseKeyBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) ActivateBtn_Click(sender, new RoutedEventArgs());
        }

        private void BuyNowBtn_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo(StoreUrl) { UseShellExecute = true });
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ShowError(string message)
        {
            StatusText.Foreground = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#ff6b6b"));
            StatusText.Text = message;
        }
    }
}
