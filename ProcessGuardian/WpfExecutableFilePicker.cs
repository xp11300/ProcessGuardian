using System.Windows;
using Microsoft.Win32;
using ProcessGuardian.ViewModels;

namespace ProcessGuardian;

internal sealed class WpfExecutableFilePicker(Window owner) : IExecutableFilePicker
{
    public string? SelectExecutable()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select an application",
            Filter = "Windows applications (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false,
        };

        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }
}
