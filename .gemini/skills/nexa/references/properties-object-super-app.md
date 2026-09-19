---
name: properties-object-super-app
description: Configurable super app properties
---

Use this file to select editable properties, defaults, and valid options for this target

---

# GENERAL
Include [General](./properties-common.md) properties

## Super App Identifier
Identifier used by the Mini App Center for this Super App
- Type: `string`

## Super App Version
Version used to resolve published Mini Apps
- Type: `string`

## Provisioning Url
Mini App Center URL used to retrieve published Mini Apps
- Type: `string`

## Android Public Key File
File used to verify Mini App signatures on Android
- Type: `string`

## iOS Public Key File
File used to verify Mini App signatures on Apple
- Type: `string`

## Main Object
Main object of the Super App
- Type: `string`

## Maximum Mini apps count
Maximum number of cached Mini Apps; `0` means no limit
- Type: `integer`
- Default: `0`

## Number of days to keep
Maximum cached Mini App age in days
- Type: `integer`
