
using System;
using System.Threading;
using System.Windows;
class Probe {
  [STAThread]
  static void Main() {
    try {
      var app = new WinShare.App.App();
      app.InitializeComponent();
      var view = new WinShare.App.Views.DashboardPageView();
      Console.WriteLine("CONSTRUCT OK: " + (view.Content == null ? "content=null" : view.Content.GetType().Name));
    } catch (Exception e) {
      Console.WriteLine("CONSTRUCT FAIL: " + e.GetType().Name + ": " + e.Message);
    }
  }
}