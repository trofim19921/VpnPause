# Presses a button by its caption in the VpnPause window via UI Automation (for manual UI checks).
param([Parameter(Mandatory)][string]$Name)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$p = Get-Process VpnPause | Where-Object { $_.SessionId -ne 0 -and $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { throw 'no VpnPause window' }
$root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
$el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
if (-not $el) { throw "button not found: $Name" }
$pattern = $null
if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke() }
elseif ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { $pattern.Select() }
else { throw "cannot press: $Name" }
"clicked: $Name"
