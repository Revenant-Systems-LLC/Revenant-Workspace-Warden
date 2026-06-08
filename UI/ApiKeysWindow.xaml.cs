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
            
            if (SecretsManager.HasEncryptedKey("GEMINI_API_KEY")) GeminiKeyBox.Password = "********";
            if (SecretsManager.HasEncryptedKey("MEGALLM_API_KEY")) MegaLlmKeyBox.Password = "********";
            if (SecretsManager.HasEncryptedKey("ANTHROPIC_API_KEY")) AnthropicKeyBox.Password = "********";
            if (SecretsManager.HasEncryptedKey("OPENAI_API_KEY")) OpenAIKeyBox.Password = "********";
            if (SecretsManager.HasEncryptedKey("OPENROUTER_API_KEY")) OpenRouterKeyBox.Password = "********";
            if (SecretsManager.HasEncryptedKey("GROQ_API_KEY")) GroqKeyBox.Password = "********";
            if (SecretsManager.HasEncryptedKey("XAI_API_KEY")) XaiKeyBox.Password = "********";
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            if (GeminiKeyBox.Password != "********" && !string.IsNullOrWhiteSpace(GeminiKeyBox.Password))
                SecretsManager.SaveEncryptedKey("GEMINI_API_KEY", GeminiKeyBox.Password);

            if (MegaLlmKeyBox.Password != "********" && !string.IsNullOrWhiteSpace(MegaLlmKeyBox.Password))
                SecretsManager.SaveEncryptedKey("MEGALLM_API_KEY", MegaLlmKeyBox.Password);

            if (AnthropicKeyBox.Password != "********" && !string.IsNullOrWhiteSpace(AnthropicKeyBox.Password))
                SecretsManager.SaveEncryptedKey("ANTHROPIC_API_KEY", AnthropicKeyBox.Password);

            if (OpenAIKeyBox.Password != "********" && !string.IsNullOrWhiteSpace(OpenAIKeyBox.Password))
                SecretsManager.SaveEncryptedKey("OPENAI_API_KEY", OpenAIKeyBox.Password);

            if (OpenRouterKeyBox.Password != "********" && !string.IsNullOrWhiteSpace(OpenRouterKeyBox.Password))
                SecretsManager.SaveEncryptedKey("OPENROUTER_API_KEY", OpenRouterKeyBox.Password);

            if (GroqKeyBox.Password != "********" && !string.IsNullOrWhiteSpace(GroqKeyBox.Password))
                SecretsManager.SaveEncryptedKey("GROQ_API_KEY", GroqKeyBox.Password);

            if (XaiKeyBox.Password != "********" && !string.IsNullOrWhiteSpace(XaiKeyBox.Password))
                SecretsManager.SaveEncryptedKey("XAI_API_KEY", XaiKeyBox.Password);

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
