using System;
using System.Windows;
using RevenantWorkspaceWarden.Providers;

namespace RevenantWorkspaceWarden.UI
{
    public partial class ApiKeysWindow : Window
    {
        public ApiKeysWindow()
        {
            InitializeComponent();
            
            // Populate password boxes with dummy text if a key already exists
            if (SecretsManager.HasEncryptedKey("GEMINI_API_KEY"))
            {
                GeminiKeyBox.Password = "********";
            }
            if (SecretsManager.HasEncryptedKey("ANTHROPIC_API_KEY"))
            {
                MegaLlmKeyBox.Password = "********";
            }
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            if (GeminiKeyBox.Password != "********" && !string.IsNullOrWhiteSpace(GeminiKeyBox.Password))
            {
                SecretsManager.SaveEncryptedKey("GEMINI_API_KEY", GeminiKeyBox.Password);
            }

            if (MegaLlmKeyBox.Password != "********" && !string.IsNullOrWhiteSpace(MegaLlmKeyBox.Password))
            {
                SecretsManager.SaveEncryptedKey("ANTHROPIC_API_KEY", MegaLlmKeyBox.Password);
            }

            MessageBox.Show("Keys have been successfully encrypted and stored in your local Windows profile vault.", "Secrets Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
            Close();
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
