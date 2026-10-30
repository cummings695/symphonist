# Shows one Windows toast. Reads {"title","body","link"} from standard input; exits non-zero with a message on error.
$ErrorActionPreference = 'Stop'
try {
    $json = [Console]::In.ReadToEnd() | ConvertFrom-Json
    $appId = 'Symphonist.Host'
    $key = "HKCU:\Software\Classes\AppUserModelId\$appId"
    if (-not (Test-Path $key)) {
        New-Item -Path $key -Force | Out-Null
        New-ItemProperty -Path $key -Name DisplayName -Value 'Symphonist' -PropertyType String -Force | Out-Null
    }

    $title = [System.Security.SecurityElement]::Escape([string]$json.title)
    $body = [System.Security.SecurityElement]::Escape([string]$json.body)
    $link = [System.Security.SecurityElement]::Escape([string]$json.link)
    $xml = "<toast activationType=`"protocol`" launch=`"$link`"><visual><binding template=`"ToastGeneric`"><text>$title</text><text>$body</text></binding></visual></toast>"

    [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
    [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null
    $doc = New-Object Windows.Data.Xml.Dom.XmlDocument
    $doc.LoadXml($xml)
    $toast = [Windows.UI.Notifications.ToastNotification]::new($doc)
    [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($appId).Show($toast)
}
catch {
    [Console]::Error.WriteLine("toast failed: $($_.Exception.Message)")
    exit 1
}
