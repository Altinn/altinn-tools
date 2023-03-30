# Platform restore tool: Altinn ReStorage

## Prereqisites
In order to use this tool simply clone the repository to your local machine.
Add appsettings.json and run the application.

appsettings.json contains configuration for the various storage accounts and cosmos db.
An updated version of this file is available on [AltinnPedia](https://pedia.altinn.cloud/altinn-3/ops/backup-recovery/altinn_restorage/) (requires authentication).

## Root command: settings

### SubCommand: update

Set environment context

`settings update -e [environment]`

Valid environments: at22, at23, at24, tt02, prod

### SubCommand: login / logout

Authenticate yourself using Azure AD

`settings login`

`settings logout`

## Root command: data

### SubCommand: list

This command lists data elements of an instance

`data list [parameters]`

Required parameters:

`--instanceId` (-iid) or `--instanceGuid` (-ig)

Optional paramters:

`--dataState` (-ds) _valid values: all, deleted, active_\*

`--organisation` (-org)\*\*

`--application` (-app)\*\*

\* active is default if nothing is specified

\*\* org/app is required if retrieving deleted data elements

#### Examples: data list

All examples are based on data in TT02

List all active data elements of an instance:
`data list -ig e415477d-7964-4ffe-97b1-4b2cbf7ba8fe`

List all deleted elements of an instance:
`data list -ig e415477d-7964-4ffe-97b1-4b2cbf7ba8fe -org ttd -app apps-test -ds deleted`

List both active and deleted elements of an instance:
`data list -ig e415477d-7964-4ffe-97b1-4b2cbf7ba8fe -org ttd -app apps-test -ds all`

#### SubCommand: info

This command show information for a data element

`data info [arguments] [options]`

Required paramters:
`--dataGuid` (-dg)

Optional parameters:

`--instanceId` (-iid) or `--instanceGuid` (-ig) \*

`--organisation` (-org)\*

`--application` (-app)\*

\* org, app, instanceId are required if retrieving version history for a data element

Options:

`--exclude-metadata` (-em)

`--list-versions` (-lv)

#### Examples: data info

All examples are based on data in TT02

Get metadata for a data element:
`data info -ig e415477d-7964-4ffe-97b1-4b2cbf7ba8fe -dg 8a7c300d-4163-4566-99a3-9aa2fe538aef`

List previous versions of a data element:
`data info -ig e415477d-7964-4ffe-97b1-4b2cbf7ba8fe -dg e17d5140-dafe-47ae-a033-4ab309a5a489 -org ttd -app apps-test -lv -em`

Get metadata and list previous versions of a data element:
`data info -ig e415477d-7964-4ffe-97b1-4b2cbf7ba8fe -dg e17d5140-dafe-47ae-a033-4ab309a5a489 -org ttd -app apps-test -lv`

#### SubCommand: undelete

This command undeletes a deleted data element

`data undelete [parameters]`

Required parameters:

`--dataGuid` (-dg)

`--instanceId` (-iid) or `--instanceGuid` (-ig)

`--organisation` (-org)

`--application` (-app)

#### Examples: data undelete

All examples are based on data in TT02

`data undelete -ig 374e360e-f03c-4fce-b37b-78b21a51401c -dg 5d52a7cf-1ed9-4664-ab13-c074586c8f5c -org ttd -app apps-test`

#### SubCommand: restore

This command restores an active data element to a previous version

`data restore [parameters]`

Required parameters:

`--dataGuid` (-dg)

`--instanceId` (-iid) or `--instanceGuid` (-ig)

`--organisation` (-org)

`--application` (-app)

`--restoreTimestamp` (-rt)

#### Examples: data restore

All examples are based on data in TT02

`data restore -ig 374e360e-f03c-4fce-b37b-78b21a51401c -dg 5d52a7cf-1ed9-4664-ab13-c074586c8f5c -org ttd -app apps-test -rt 2020-08-14T08:47:51.6247551Z`
