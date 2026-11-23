# Closed allow-list for manual helper calls.
# The AllowWrite switch defaults to false; this function returns true only for
# a read-only catalog hint or an exact whitelist row.
# ReadOnlyHint true is trusted from the Hub catalog, not from the caller.

function Test-GxHubManualCallPermitted {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$ToolName,
        [Parameter(Mandatory)][AllowEmptyString()][string]$ArgumentsJson,
        [switch]$ReadOnlyHint
    )

    if ($ReadOnlyHint) {
        return $true
    }

    # The properties tool groups reads and writes under one catalog entry, so the catalog hint is false. A property READ
    # (action=get) is permitted by its own closed list of arguments below; every other call needs -AllowWrite.
    if ($ToolName -ceq 'native_genexus_properties') {
        return Test-GxHubPropertiesGetPermitted -ArgumentsJson $ArgumentsJson
    }

    $fields = ConvertTo-ClosedArgumentFields -Json $ArgumentsJson
    if ($null -eq $fields) {
        return $false
    }

    $action = $null
    $force = $null
    $dryRun = $null
    $wait = $null
    $freshness = $null
    foreach ($name in @($fields.Keys)) {
        if ($name -ceq 'action') { $action = $fields[$name]; continue }
        if ($name -ceq 'force') { $force = $fields[$name]; continue }
        if ($name -ceq 'dryRun') { $dryRun = $fields[$name]; continue }
        if ($name -ceq 'wait') { $wait = $fields[$name]; continue }
        if ($name -ceq 'freshness') { $freshness = $fields[$name]; continue }
        return $false
    }

    if ($force -eq 'bool:true') {
        return $false
    }
    if ($null -ne $force -and $force -ne 'bool:false') {
        return $false
    }

    if ($ToolName -ceq 'native_genexus_connection_recover' -and $action -ceq 'string:journal_status') {
        return $null -eq $dryRun -and $null -eq $wait -and $null -eq $freshness
    }

    if ($ToolName -ceq 'native_genexus_connection_recover' -and $action -ceq 'string:journal_repair') {
        return $dryRun -ceq 'bool:true' -and $null -eq $wait -and $null -eq $freshness
    }

    if ($ToolName -ceq 'native_genexus_kb_version' -and $action -ceq 'string:list') {
        return $null -eq $force -and $null -eq $dryRun -and $null -eq $wait -and $null -eq $freshness
    }

    if ($ToolName -ceq 'native_genexus_lifecycle' -and $action -ceq 'string:status') {
        return $null -eq $force -and $null -eq $dryRun -and $wait -ceq 'number:30' -and $freshness -ceq 'string:current'
    }

    return $false
}

# A property read for the manual helper: action exactly 'get' and only argument names that cannot write or clear a fence.
# Strings only (propertyNames: an array of strings). Not on the list, so denied: value, properties, expectedVersion, baseVersion,
# dryRun, rollbackOnFailure, validationMode, reconcileTimedOutWrite, targets, destination and every other name; a duplicate
# name, a non-string value, a different action or malformed JSON is denied too.
function Test-GxHubPropertiesGetPermitted {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$ArgumentsJson)

    $allowedStrings = @('action', 'name', 'type', 'propertyName', 'query', 'projection', 'kb', 'control')
    $document = $null
    try {
        $document = [System.Text.Json.JsonDocument]::Parse($ArgumentsJson)
        $root = $document.RootElement
        if ($root.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
            return $false
        }

        $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $action = $null
        foreach ($property in $root.EnumerateObject()) {
            if (-not $seen.Add($property.Name)) {
                return $false
            }
            if ($property.Name -ceq 'propertyNames') {
                if ($property.Value.ValueKind -ne [System.Text.Json.JsonValueKind]::Array) {
                    return $false
                }
                foreach ($item in $property.Value.EnumerateArray()) {
                    if ($item.ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
                        return $false
                    }
                }
                continue
            }
            if ($allowedStrings -cnotcontains $property.Name) {
                return $false
            }
            if ($property.Value.ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
                return $false
            }
            if ($property.Name -ceq 'action') {
                $action = $property.Value.GetString()
            }
        }
        return $action -ceq 'get'
    }
    catch {
        return $false
    }
    finally {
        if ($null -ne $document) {
            $document.Dispose()
        }
    }
}

function ConvertTo-ClosedArgumentFields {
    param([string]$Json)

    $document = $null
    try {
        $document = [System.Text.Json.JsonDocument]::Parse($Json)
        if ($document.RootElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
            return $null
        }

        $fields = [System.Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
        foreach ($property in $document.RootElement.EnumerateObject()) {
            if ($fields.ContainsKey($property.Name)) {
                return $null
            }
            $encoded = ConvertTo-ClosedFieldValue -Element $property.Value
            if ($null -eq $encoded) {
                return $null
            }
            $fields[$property.Name] = $encoded
        }
        return $fields
    }
    catch {
        return $null
    }
    finally {
        if ($null -ne $document) {
            $document.Dispose()
        }
    }
}

function ConvertTo-ClosedFieldValue {
    param($Element)

    switch ($Element.ValueKind) {
        ([System.Text.Json.JsonValueKind]::String) { return 'string:' + $Element.GetString() }
        ([System.Text.Json.JsonValueKind]::True) { return 'bool:true' }
        ([System.Text.Json.JsonValueKind]::False) { return 'bool:false' }
        ([System.Text.Json.JsonValueKind]::Number) {
            if ($Element.GetRawText() -ceq '30') { return 'number:30' }
            return $null
        }
        default { return $null }
    }
}
