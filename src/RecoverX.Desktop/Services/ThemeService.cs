using System;
using System.Windows;
using RecoverX.Core.Models;

namespace RecoverX.Desktop.Services
{
    public interface IThemeService { void Apply(string theme); }

    public sealed class ThemeService : IThemeService
    {
        public void Apply(string theme)
        {
            string name = theme == AppSettings.DarkTheme ? "Dark" : "Light";
            var dict = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/RecoverX.Desktop;component/Themes/" + name + ".xaml")
            };
            Application.Current.Resources.MergedDictionaries[0] = dict;
        }
    }
}
