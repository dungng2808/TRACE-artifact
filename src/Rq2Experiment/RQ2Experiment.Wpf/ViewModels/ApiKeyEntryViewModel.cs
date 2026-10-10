using System.Security.Cryptography;
using System.Text;

namespace RQ2Experiment.Wpf.ViewModels;

public sealed class ApiKeyEntryViewModel
{
    internal ApiKeyEntryViewModel(string secret, int sequence)
    {
        Secret = secret;
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)))[..8];
        DisplayText = $"Key {sequence}  •  fingerprint {fingerprint}";
    }

    internal string Secret { get; }
    public string DisplayText { get; }
}
