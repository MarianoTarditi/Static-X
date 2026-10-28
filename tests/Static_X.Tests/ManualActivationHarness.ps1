# Offline transport/prompt harness: never contacts HTTP or puts credentials in process arguments.
param([string]$HelperPath, [switch]$FailActivation, [switch]$LoginOnly)
$ErrorActionPreference = 'Stop'
$expectedRecord = Get-Content -LiteralPath (Get-ChildItem -LiteralPath $env:Activation__DevelopmentPickupPath -Filter '*.json').FullName -Raw | ConvertFrom-Json
$expectedToken = ([Uri]$expectedRecord.Link).Fragment.Substring(12)
$expectedPassword = [Guid]::NewGuid().ToString('N') + 'Aa1!' + [char]0x00F1 + [char]0x00E1 + [char]0x6F22
$strictUtf8 = New-Object System.Text.UTF8Encoding($false, $true)
$anonymousCsrf = [Guid]::NewGuid().ToString('N')
$authenticatedCsrf = [Guid]::NewGuid().ToString('N')
$csrfCalls = 0
$activated = $LoginOnly.IsPresent
$loggedIn = $false
$observedSession = $null
$activationRequests = 0
$loginRequests = 0
$validatedActivationBytes = $false
if ($LoginOnly) {
    # A subsequent login must work without any pickup record to consume.
    Get-ChildItem -LiteralPath $env:Activation__DevelopmentPickupPath -Filter '*.json' | Remove-Item -Force
}

function Read-RequestJson {
    param($Bytes, [string]$MediaType)
    if ($Bytes -isnot [byte[]] -or $MediaType -cne 'application/json; charset=utf-8') {
        throw 'An explicit UTF-8 byte body and charset are required.'
    }
    # Model server deserialization from the transmitted bytes, never the original string.
    return $strictUtf8.GetString($Bytes) | ConvertFrom-Json
}

function Read-Host {
    param([string]$Prompt, [switch]$AsSecureString)
    if ($AsSecureString) { return ConvertTo-SecureString $expectedPassword -AsPlainText -Force }
    if ($Prompt.StartsWith('Gym ID')) { return '7' }
    if ($Prompt.StartsWith('Email')) { return $expectedRecord.Email }
    throw 'Unexpected prompt.'
}

function Invoke-RestMethod {
    param([Uri]$Uri, $WebSession, $Headers, [string]$Method, [string]$ContentType, $Body, [switch]$Verbose, [switch]$Debug)
    if ($Uri.Scheme -ne 'https' -or -not $Uri.IsLoopback -or $Uri.Query) { throw 'Unsafe request.' }
    if ($null -eq $script:observedSession) { $script:observedSession = $WebSession }
    if (-not [object]::ReferenceEquals($WebSession, $script:observedSession)) { throw 'Session changed.' }
    switch ($Uri.AbsolutePath) {
        '/api/account/csrf' {
            $script:csrfCalls++
            if ($script:loggedIn) { return @{ token = $authenticatedCsrf } }
            return @{ token = $anonymousCsrf }
        }
        '/api/account/activation' {
            $script:activationRequests++
            if ($LoginOnly) { throw 'Login-only must not activate.' }
            $data = Read-RequestJson -Bytes $Body -MediaType $ContentType
            if ($Method -ne 'Post' -or
                $Headers['X-CSRF-TOKEN'] -cne $anonymousCsrf -or
                $data.token -cne $expectedToken -or $data.password -cne $expectedPassword) { throw 'Incorrect activation.' }
            $script:validatedActivationBytes = $true
            if ($FailActivation) { throw 'Synthetic transport failure.' }
            $script:activated = $true
            return
        }
        '/api/account/login' {
            $script:loginRequests++
            $data = Read-RequestJson -Bytes $Body -MediaType $ContentType
            if (-not $script:activated -or $Method -ne 'Post' -or
                $Headers['X-CSRF-TOKEN'] -cne $anonymousCsrf -or $data.gymId -ne 7 -or
                $data.email -cne $expectedRecord.Email -or $data.password -cne $expectedPassword) { throw 'Incorrect login.' }
            $script:loggedIn = $true
            return
        }
        '/api/account/me' {
            if (-not $script:loggedIn) { throw 'Not authenticated.' }
            return @{ gymId = 7; email = $expectedRecord.Email }
        }
        default { throw 'Unexpected HTTP request.' }
    }
}

try {
    . $HelperPath -LoginOnly:$LoginOnly
    if ($FailActivation -or -not $loggedIn -or $csrfCalls -ne 2 -or
        $loginRequests -ne 1 -or $activationRequests -ne [int](-not $LoginOnly) -or
        $StaticXHeaders['X-CSRF-TOKEN'] -cne $authenticatedCsrf -or
        @(Get-ChildItem -LiteralPath $env:Activation__DevelopmentPickupPath -Filter '*.json').Count -ne 0) { throw 'Incorrect helper outcome.' }
    if (Get-Variable password, securePassword, token, payload, pickup -ErrorAction SilentlyContinue) { throw 'Credential variables retained.' }
    'Offline helper verified.'
}
catch {
    if ($FailActivation -and $validatedActivationBytes -and $activationRequests -eq 1 -and $loginRequests -eq 0 -and
        $_.Exception.Message.StartsWith('Local activation/login failed.') -and
        -not $loggedIn -and @(Get-ChildItem -LiteralPath $env:Activation__DevelopmentPickupPath -Filter '*.json').Count -eq 1 -and
        -not (Get-Variable password, securePassword, token, payload, pickup -ErrorAction SilentlyContinue)) {
        'Offline failure sanitization verified.'
        exit 0
    }
    # Test failures must not include request bodies, passwords or tokens.
    [Console]::Error.WriteLine('Offline helper verification failed.')
    exit 1
}
