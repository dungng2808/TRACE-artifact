using System.Windows;

namespace Rq1Experiment.Wpf;

public partial class ExperimentNameDialog : Window
{
    public ExperimentNameDialog(string suggestedName)
    {
        InitializeComponent();
        ExperimentNameTextBox.Text = suggestedName;
        Loaded += (_, _) =>
        {
            ExperimentNameTextBox.Focus();
            ExperimentNameTextBox.SelectAll();
        };
    }

    public string ExperimentName => ExperimentNameTextBox.Text.Trim();

    private void Create_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ExperimentName))
        {
            ValidationText.Text = "Enter an experiment name.";
            return;
        }
        if (ExperimentName.Any(character =>
                !(char.IsLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            ValidationText.Text = "Only letters, digits, '.', '_' and '-' are allowed.";
            return;
        }
        DialogResult = true;
    }
}
