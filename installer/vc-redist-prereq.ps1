# Shared Visual C++ Redistributable prerequisite settings for the NSIS bootstrapper.
# PHP builds from windows.php.net (VS17 / VC++ 14.x) require VCRUNTIME140.dll at runtime.
$script:VcRedistInstallerUrl = 'https://aka.ms/vs/17/release/vc_redist.x64.exe'
$script:VcRedistInstallerFileName = 'vc_redist.x64.exe'
