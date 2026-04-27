using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Aion2Meter.ViewModels;

/// <summary>
/// MVVM의 ViewModel 기반 클래스.
/// 
/// INotifyPropertyChanged: 프로퍼티 변경 시 WPF 바인딩 엔진에 알림.
/// Java의 PropertyChangeSupport와 동일한 역할.
/// 
/// CallerMemberName 특성 덕분에 SetProperty 호출 시 
/// 프로퍼티 이름을 문자열로 하드코딩할 필요 없음 → 리팩토링 안전.
/// </summary>
public abstract class BaseViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

/// <summary>
/// ICommand 구현체. 버튼 클릭 등의 커맨드를 ViewModel에서 처리.
/// Java의 ActionListener와 유사하지만 CanExecute로 버튼 활성화 제어 가능.
/// </summary>
public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute == null ? null : _ => canExecute()) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
}
