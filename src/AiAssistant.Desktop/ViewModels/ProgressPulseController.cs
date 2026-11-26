using Avalonia.Threading;

namespace AiAssistant.Desktop.ViewModels;

/// <summary>
/// Drives a determinate 0..100 cycling progress value while busy. A determinate bar keeps
/// advancing after its tab is hidden and re-shown, unlike an indeterminate bar whose animation
/// freezes when the Avalonia tab content is detached.
/// </summary>
internal sealed class ProgressPulseController : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Action<double> _setPulse;
    private int _value;
    private bool _disposed;

    public ProgressPulseController(Action<double> setPulse)
    {
        _setPulse = setPulse ?? throw new ArgumentNullException(nameof(setPulse));
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Normal, OnTick);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _value = (_value + 2) % 101;
        _setPulse(_value);
    }

    public void SetBusy(bool busy)
    {
        if (_disposed)
        {
            return;
        }

        if (busy)
        {
            _value = 0;
            if (!_timer.IsEnabled)
            {
                _timer.Start();
            }
        }
        else
        {
            if (_timer.IsEnabled)
            {
                _timer.Stop();
            }

            _value = 0;
        }

        _setPulse(_value);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_timer.IsEnabled)
        {
            _timer.Stop();
        }
    }
}
