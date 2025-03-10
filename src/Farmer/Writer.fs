module Farmer.Writer

open Farmer.Internal
open Newtonsoft.Json
open System
open System.IO

module Outputters =
    let private containerAccess (a:StorageContainerAccess) =
        match a with
        | Private -> "None"
        | Container -> "Container"
        | Blob -> "Blob"

    let private storageAccountContainer (parent:StorageAccount) (name, access) = {|
        ``type`` = "blobServices/containers"
        apiVersion = "2018-03-01-preview"
        name = "default/" + name
        dependsOn = [ parent.Name.Value ]
        properties = {| publicAccess = containerAccess access |}
    |}
    
    let storageAccount (resource:StorageAccount) = {|
        ``type`` = "Microsoft.Storage/storageAccounts"
        sku = {| name = resource.Sku |}
        kind = "StorageV2"
        name = resource.Name.Value
        apiVersion = "2018-07-01"
        location = resource.Location
        resources = resource.Containers |> List.map (storageAccountContainer resource)
    |}
    
    let appInsights (resource:AppInsights) = {|
        ``type`` = "Microsoft.Insights/components"
        kind = "web"
        name = resource.Name.Value
        location = resource.Location
        apiVersion = "2014-04-01"
        tags =
            [ match resource.LinkedWebsite with
              | Some linkedWebsite -> yield sprintf "[concat('hidden-link:', resourceGroup().id, '/providers/Microsoft.Web/sites/', '%s')]" linkedWebsite.Value, "Resource"
              | None -> ()
              yield "displayName", "AppInsightsComponent" ]
            |> Map.ofList
        properties =
         match resource.LinkedWebsite with
         | Some linkedWebsite ->
            {| name = resource.Name.Value
               Application_Type = "web"
               ApplicationId = linkedWebsite.Value |} |> box
         | None ->
            {| name = resource.Name.Value
               Application_Type = "web" |} |> box
    |}
    let serverFarm (farm:ServerFarm) =
        let baseProps =
            {| ``type`` = "Microsoft.Web/serverfarms"
               sku =
                   let baseProps =
                       {| name = farm.Sku
                          tier = farm.Tier
                          size = farm.WorkerSize |}
                   if farm.IsDynamic then box {| baseProps with family = "Y"; capacity = 0 |}
                   else box {| baseProps with
                                   capacity = farm.WorkerCount |}
               name = farm.Name.Value
               apiVersion = "2018-02-01"
               location = farm.Location
               properties =
                   if farm.IsDynamic then
                       box {| name = farm.Name.Value
                              computeMode = "Dynamic" |}
                   else
                       box {| name = farm.Name.Value
                              perSiteScaling = false
                              reserved = false |}
            |}
        match farm.Kind with
        | Some kind -> box {| baseProps with kind = kind |}
        | None -> box baseProps
    let webApp (webApp:WebApp) = {|
        ``type`` = "Microsoft.Web/sites"
        name = webApp.Name.Value
        apiVersion = "2016-08-01"
        location = webApp.Location
        dependsOn = webApp.Dependencies |> List.map(fun p -> p.Value)
        kind = webApp.Kind
        resources =
            webApp.Extensions
            |> Set.toList
            |> List.map (function
            | AppInsightsExtension ->
                 {| apiVersion = "2016-08-01"
                    name = "Microsoft.ApplicationInsights.AzureWebSites"
                    ``type`` = "siteextensions"
                    dependsOn = [ webApp.Name.Value ]
                    properties = {||}
                 |})
        properties =
            {| serverFarmId = webApp.ServerFarm.Value
               siteConfig =
                    [ yield "alwaysOn", box webApp.AlwaysOn
                      yield "appSettings", webApp.AppSettings |> List.map(fun (k,v) -> {| name = k; value = v |}) |> box                      
                      match webApp.LinuxFxVersion with Some v -> yield "linuxFxVersion", box v | None -> ()
                      match webApp.NetFrameworkVersion with Some v -> yield "netFrameworkVersion", box v | None -> ()
                      match webApp.JavaVersion with Some v -> yield "javaVersion", box v | None -> ()
                      match webApp.JavaContainer with Some v -> yield "javaContainer", box v | None -> ()
                      match webApp.JavaContainerVersion with Some v -> yield "javaContainerVersion", box v | None -> ()
                      match webApp.PhpVersion with Some v -> yield "phpVersion", box v | None -> ()
                      match webApp.PythonVersion with Some v -> yield "pythonVersion", box v | None -> ()
                      yield "metadata", webApp.Metadata |> List.map(fun (k,v) -> {| name = k; value = v |}) |> box ]
                    |> Map.ofList
             |}
    |}
    let cosmosDbContainer (container:CosmosDbContainer) = {|
        ``type`` = "Microsoft.DocumentDb/databaseAccounts/apis/databases/containers"
        name = sprintf "%s/sql/%s/%s" container.Account.Value container.Database.Value container.Name.Value
        apiVersion = "2016-03-31"
        dependsOn = [ container.Database.Value ]
        properties =
            {| resource =
                {| id = container.Name.Value
                   partitionKey =
                    {| paths = container.PartitionKey.Paths
                       kind = string container.PartitionKey.Kind |}
                   indexingPolicy =
                    {| indexingMode = "consistent"
                       includedPaths =
                           container.IndexingPolicy.IncludedPaths
                           |> List.map(fun p ->
                            {| path = p.Path
                               indexes =
                                p.Indexes
                                |> List.map(fun i ->
                                    {| kind = string i.Kind
                                       dataType = (string i.DataType).ToLower()
                                       precision = -1 |})
                            |})
                       excludedPaths =
                        container.IndexingPolicy.ExcludedPaths
                        |> List.map(fun p -> {| path = p |})
                    |}
                |}
            |}
    |}
    let cosmosDbServer (cosmosDb:CosmosDbAccount) = {|
        ``type`` = "Microsoft.DocumentDB/databaseAccounts"
        name = cosmosDb.Name.Value
        apiVersion = "2016-03-31"
        location = cosmosDb.Location
        kind = "GlobalDocumentDB"
        properties =
            let baseProps =
                let consistencyPolicy =
                    match cosmosDb.ConsistencyPolicy with
                    | BoundedStaleness(maxStaleness, maxInterval) ->
                        box {| defaultConsistencyLevel = "BoundedStaleness"
                               maxStalenessPrefix = maxStaleness
                               maxIntervalInSeconds = maxInterval |}
                    | Session
                    | Eventual
                    | ConsistentPrefix
                    | Strong ->
                        box {| defaultConsistencyLevel = string cosmosDb.ConsistencyPolicy |}
                {| consistencyPolicy = consistencyPolicy
                   databaseAccountOfferType = "Standard" |}
            match cosmosDb.WriteModel with
            | AutoFailover secondary ->
                {| baseProps with
                      enableAutomaticFailover = true
                      locations = [
                        {| locationName = cosmosDb.Location; failoverPriority = 0 |}
                        {| locationName = secondary; failoverPriority = 1 |}
                      ]
                |} |> box
            | MultiMaster secondary ->
                {| baseProps with
                      autoenableMultipleWriteLocations = true
                      locations = [
                        {| locationName = cosmosDb.Location; failoverPriority = 0 |}
                        {| locationName = secondary; failoverPriority = 1 |}
                      ]
                |} |> box
            | NoFailover ->
                box baseProps
    |}
    let cosmosDbSql (cosmosDbSql:CosmosDbSql) = {|
        ``type`` = "Microsoft.DocumentDB/databaseAccounts/apis/databases"
        name = sprintf "%s/sql/%s" cosmosDbSql.Account.Value cosmosDbSql.Name.Value
        apiVersion = "2016-03-31"
        dependsOn = [ cosmosDbSql.Account.Value ]
        properties =
            {| resource = {| id = cosmosDbSql.Name.Value |}
               options = {| throughput = cosmosDbSql.Throughput |} |}
    |}
    let sqlAzure (database:SqlAzure) = {|
        ``type`` = "Microsoft.Sql/servers"
        name = database.ServerName.Value
        apiVersion = "2014-04-01-preview"
        location = database.Location
        tags = {| displayName = "SqlServer" |}
        properties =
            {| administratorLogin = database.Credentials.Username
               administratorLoginPassword = database.Credentials.Password.AsArmRef
               version = "12.0" |}
        resources = [
            yield
                {| ``type`` = "databases"
                   name = database.DbName.Value               
                   apiVersion = "2015-01-01"
                   location = database.Location
                   tags = {| displayName = "Database" |}
                   properties =
                    {| edition = database.DbEdition
                       collation = database.DbCollation
                       requestedServiceObjectiveName = database.DbObjective |}
                   dependsOn = [
                       database.ServerName.Value
                   ]
                   resources = [
                       {| ``type`` = "transparentDataEncryption"
                          comments = "Transparent Data Encryption"
                          name = "current"                      
                          apiVersion = "2014-04-01-preview"
                          properties = {| status = string database.TransparentDataEncryption |}
                          dependsOn = [ database.DbName.Value ]
                       |}
                   ]
                |} |> box
            yield!
                database.FirewallRules
                |> List.map(fun fw ->
                    {| ``type`` = "firewallrules"
                       name = fw.Name
                       apiVersion = "2014-04-01"
                       location = database.Location
                       properties =
                        {| endIpAddress = string fw.Start
                           startIpAddress = string fw.End |}
                       dependsOn = [ database.ServerName.Value ]
                    |} |> box)
        ]
    |}
    let publicIpAddress (ipAddress:VM.PublicIpAddress) = {|
        ``type`` = "Microsoft.Network/publicIPAddresses"
        apiVersion = "2018-11-01"
        name = ipAddress.Name.Value
        location = ipAddress.Location
        properties =
           match ipAddress.DomainNameLabel with
           | Some label ->
               box
                   {| publicIPAllocationMethod = "Dynamic"
                      dnsSettings = {| domainNameLabel = label.ToLower() |}
                   |}
           | None ->
               box {| publicIPAllocationMethod = "Dynamic" |}
    |}
    let virtualNetwork (vnet:VM.VirtualNetwork) = {|
        ``type`` = "Microsoft.Network/virtualNetworks"
        apiVersion = "2018-11-01"
        name = vnet.Name.Value
        location = vnet.Location
        properties =
             {| addressSpace = {| addressPrefixes = vnet.AddressSpacePrefixes |}
                subnets =
                 vnet.Subnets
                 |> List.map(fun subnet ->
                    {| name = subnet.Name.Value
                       properties = {| addressPrefix = subnet.Prefix |}
                    |})
             |}
    |}
    let networkInterface (nic:VM.NetworkInterface) = {|
        ``type`` = "Microsoft.Network/networkInterfaces"
        apiVersion = "2018-11-01"
        name = nic.Name.Value
        location = nic.Location
        dependsOn = [
            yield nic.VirtualNetwork.Value
            for config in nic.IpConfigs do
             yield config.PublicIpName.Value
        ]
        properties =
            {| ipConfigurations =
               nic.IpConfigs
               |> List.mapi(fun index ipConfig ->
                   {| name = sprintf "ipconfig%i" (index + 1)
                      properties =
                       {| privateIPAllocationMethod = "Dynamic"
                          publicIPAddress = {| id = sprintf "[resourceId('Microsoft.Network/publicIPAddresses','%s')]" ipConfig.PublicIpName.Value |}
                          subnet = {| id = sprintf "[resourceId('Microsoft.Network/virtualNetworks/subnets', '%s', '%s')]" nic.VirtualNetwork.Value ipConfig.SubnetName.Value |}
                       |}
                   |})
            |}
    |}
    let virtualMachine (vm:VM.VirtualMachine) = {|
        ``type`` = "Microsoft.Compute/virtualMachines"
        apiVersion = "2018-10-01"
        name = vm.Name.Value
        location = vm.Location
        dependsOn = [
            yield vm.NetworkInterfaceName.Value
            match vm.StorageAccount with
            | Some s -> yield s.Value
            | None -> ()
        ]
        properties =
         {| hardwareProfile = {| vmSize = vm.Size |}
            osProfile =
             {|
                computerName = vm.Name.Value
                adminUsername = vm.Credentials.Username
                adminPassword = vm.Credentials.Password.AsArmRef
             |}
            storageProfile =
             let vmNameLowerCase = vm.Name.Value.ToLower()
             {| imageReference =
                 {| publisher = vm.Image.Publisher
                    offer = vm.Image.Offer
                    sku = vm.Image.Sku
                    version = "latest" |}
                osDisk =
                 {| createOption = "FromImage"
                    name = sprintf "%s-osdisk" vmNameLowerCase
                    diskSizeGB = vm.OsDisk.Size
                    managedDisk = {| storageAccountType = string vm.OsDisk.DiskType |}
                 |}
                dataDisks =
                 vm.DataDisks
                 |> List.mapi(fun lun dataDisk ->
                     {| createOption = "Empty"
                        name = sprintf "%s-datadisk-%i" vmNameLowerCase lun
                        diskSizeGB = dataDisk.Size
                        lun = lun                           
                        managedDisk = {| storageAccountType = string dataDisk.DiskType |} |})
             |}
            networkProfile =
             {| networkInterfaces =
                 [ 
                     {| id = sprintf "[resourceId('Microsoft.Network/networkInterfaces','%s')]" vm.NetworkInterfaceName.Value |}
                 ]
             |}
            diagnosticsProfile =
             match vm.StorageAccount with
             | Some storageAccount ->
                 box
                     {| bootDiagnostics =
                         {| enabled = true
                            storageUri = sprintf "[reference('%s').primaryEndpoints.blob]" storageAccount.Value
                         |}
                     |}
             | None ->
                 box {| bootDiagnostics = {| enabled = false |} |}
        |}
    |}
    let search (search:Search) = {|
        ``type`` = "Microsoft.Search/searchServices"
        apiVersion = "2015-08-19"
        name = search.Name.Value
        location = search.Location
        sku =
         {| name = search.Sku |}
        properties =
         {| replicaCount = search.ReplicaCount
            partitionCount = search.PartitionCount
            hostingMode = search.HostingMode |}
    |}

let processTemplate (template:ArmTemplate) = {|
    ``$schema`` = "https://schema.management.azure.com/schemas/2015-01-01/deploymentTemplate.json#"
    contentVersion = "1.0.0.0"
    resources =
        template.Resources
        |> List.map(function
            | AppInsights ai -> Outputters.appInsights ai |> box
            | StorageAccount s -> Outputters.storageAccount s |> box
            | ServerFarm s -> Outputters.serverFarm s |> box
            | WebApp wa -> Outputters.webApp wa |> box
            | CosmosAccount cds -> Outputters.cosmosDbServer cds |> box
            | CosmosSqlDb db -> Outputters.cosmosDbSql db |> box
            | CosmosContainer c -> Outputters.cosmosDbContainer c |> box
            | SqlServer sql -> Outputters.sqlAzure sql |> box
            | Ip address -> Outputters.publicIpAddress address |> box
            | Vnet vnet -> Outputters.virtualNetwork vnet |> box
            | Nic nic -> Outputters.networkInterface nic |> box
            | Vm vm -> Outputters.virtualMachine vm |> box
            | AzureSearch search -> Outputters.search search |> box
        )
    parameters =
        template.Resources
        |> List.choose(function
            | SqlServer sql -> Some sql.Credentials.Password
            | Vm vm -> Some vm.Credentials.Password
            | _ -> None)
        |> List.map(fun (SecureParameter p) -> p, {| ``type`` = "securestring" |})
        |> Map.ofList
    outputs =
        template.Outputs
        |> List.map(fun (k, v) ->
            k, Map [ "type", "string"
                     "value", v ])
        |> Map.ofList
|}

let toJson = processTemplate >> JsonConvert.SerializeObject
let toFile armTemplateName json =
    let templateFilename = sprintf "%s.json" armTemplateName
    File.WriteAllText(templateFilename, json)
    templateFilename
let toBatchFile armTemplateName resourceGroupName location templateFilename =
    let batchFilename = sprintf "%s.bat" armTemplateName

    let azureCliBatch =
        sprintf """az login && az group create -l %s -n %s && az group deployment create -g %s --template-file %s"""
            location
            resourceGroupName
            resourceGroupName
            templateFilename

    File.WriteAllText(batchFilename, azureCliBatch)
    batchFilename
let generateDeployScript resourceGroupName (location, template) =
    let templateName = "farmer-deploy"

    template
    |> toJson
    |> toFile templateName
    |> toBatchFile templateName resourceGroupName location
let quickDeploy resourceGroupName (location, template) =
    generateDeployScript resourceGroupName (location, template)
    |> Diagnostics.Process.Start
    |> ignore