$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut('C:\Users\27960\Desktop\时迹.lnk')
$shortcut.TargetPath = 'D:\apps\UsageTrackerNative\releases\20260505-1339\时迹.exe'
$shortcut.WorkingDirectory = 'D:\apps\UsageTrackerNative\releases\20260505-1339'
$shortcut.IconLocation = 'D:\apps\UsageTrackerNative\releases\20260505-1339\Assets\app-icon.ico'
$shortcut.Description = '时迹'
$shortcut.Save()
