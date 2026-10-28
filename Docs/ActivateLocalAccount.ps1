# Dot-source in a private PowerShell 5.1 session; never enable transcription/debug tracing.
param(
    [Uri]$BaseUri = 'https://localhost:7226',
    [switch]$LoginOnly
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$passwordPointer = [IntPtr]::Zero
$securePassword = $null
$password = $null
$payload = $null
$pickup = $null
$csrf = $null
try {
    if ($env:DOTNET_ENVIRONMENT -ne 'Development' -or -not $BaseUri.IsAbsoluteUri -or
        $BaseUri.Scheme -ne 'https' -or -not $BaseUri.IsLoopback -or $BaseUri.UserInfo -or
        $BaseUri.Query -or $BaseUri.Fragment -or $BaseUri.AbsolutePath -ne '/') {
        throw 'Unsafe local configuration.'
    }
    $StaticXBaseUri = $BaseUri.GetLeftPart([UriPartial]::Authority)
    $gymText = Read-Host 'Gym ID from the approved development database read'
    $gymId = 0
    if (-not [int]::TryParse($gymText, [ref]$gymId) -or $gymId -le 0) { throw 'Invalid gym.' }
    $email = Read-Host 'Email used for this development provision'
    if (-not $LoginOnly) {
        $directory = [IO.Path]::GetFullPath($env:Activation__DevelopmentPickupPath)
        $local = [Environment]::GetFolderPath('LocalApplicationData').TrimEnd('\') + '\'
        if (-not $directory.StartsWith($local, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe pickup.' }
        $files = @(Get-ChildItem -LiteralPath $directory -Filter '*.json' -File)
        if ($files.Count -ne 1) { throw 'Exactly one pickup record is required.' }
        $captureFile = $files[0].FullName
        $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        foreach ($path in @($directory, $captureFile)) {
            $item = Get-Item -LiteralPath $path -Force
            while ($null -ne $item) {
                if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unsafe pickup.' }
                if ($item -is [IO.FileInfo]) { $item = $item.Directory } else { $item = $item.Parent }
            }
            $acl = Get-Acl -LiteralPath $path
            if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne $owner) { throw 'Unsafe pickup.' }
            $rules = $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])
            if ($rules.Count -eq 0) { throw 'Unsafe pickup.' }
            foreach ($rule in $rules) {
                if ($rule.IdentityReference.Value -ne $owner -or $rule.AccessControlType -ne 'Allow') { throw 'Unsafe pickup.' }
            }
        }
        $pickup = Get-Content -LiteralPath $captureFile -Raw | ConvertFrom-Json
        if ($pickup.Email -cne $email) { throw 'Pickup does not match the intended account.' }
        $link = [Uri]$pickup.Link
        if ($link.Scheme -ne 'https' -or -not $link.IsLoopback -or $link.Query -or
            -not $link.Fragment.StartsWith('#activation=')) { throw 'Invalid pickup.' }
        $token = [Uri]::UnescapeDataString($link.Fragment.Substring(12))
        if ($token -notmatch '\A[0-9A-Fa-f]{64}\z') { throw 'Invalid pickup.' }
    }
    $securePassword = Read-Host 'Password (Identity rules apply)' -AsSecureString
    $passwordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword)
    $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer)
    $StaticXSession = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $csrf = Invoke-RestMethod -Uri "$StaticXBaseUri/api/account/csrf" -WebSession $StaticXSession -Verbose:$false -Debug:$false
    $headers = @{ 'X-CSRF-TOKEN' = $csrf.token }
    if (-not $LoginOnly) {
        $payload = [Text.Encoding]::UTF8.GetBytes((@{ token = $token; password = $password } | ConvertTo-Json -Compress))
        $null = Invoke-RestMethod -Method Post -Uri "$StaticXBaseUri/api/account/activation" -WebSession $StaticXSession -Headers $headers -ContentType 'application/json; charset=utf-8' -Body $payload -Verbose:$false -Debug:$false
        # Activation deliberately does not sign in. Retain anonymous CSRF cookies for login.
        Remove-Item -LiteralPath $captureFile -Force
    }
    $payload = [Text.Encoding]::UTF8.GetBytes((@{ gymId = $gymId; email = $email; password = $password } | ConvertTo-Json -Compress))
    $null = Invoke-RestMethod -Method Post -Uri "$StaticXBaseUri/api/account/login" -WebSession $StaticXSession -Headers $headers -ContentType 'application/json; charset=utf-8' -Body $payload -Verbose:$false -Debug:$false
    $StaticXAccount = Invoke-RestMethod -Uri "$StaticXBaseUri/api/account/me" -WebSession $StaticXSession -Verbose:$false -Debug:$false
    $csrf = Invoke-RestMethod -Uri "$StaticXBaseUri/api/account/csrf" -WebSession $StaticXSession -Verbose:$false -Debug:$false
    $StaticXHeaders = @{ 'X-CSRF-TOKEN' = $csrf.token }
    'Local login completed. Session and CSRF headers remain in memory; do not print them.'
}
catch {
    # Do not print HTTP exceptions, invocation bodies, pickup paths, tokens or passwords.
    Remove-Variable StaticXSession, StaticXHeaders -ErrorAction SilentlyContinue
    throw 'Local activation/login failed. Check configuration, trusted HTTPS, database gym/email, password rules and invitation expiry. If activation already succeeded, use -LoginOnly.'
}
finally {
    if ($passwordPointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer) }
    if ($null -ne $securePassword) { $securePassword.Dispose() }
    Remove-Variable password, securePassword, payload, token, pickup, link, csrf, headers, captureFile -ErrorAction SilentlyContinue
}
