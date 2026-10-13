using System.Windows;

namespace XiaoK.Host;

internal interface IPetRenderer : IDisposable
{
    FrameworkElement View { get; }
    Task InitializeAsync(CancellationToken cancellationToken);
    void SetPose(XiaoKPetPose pose);
}
