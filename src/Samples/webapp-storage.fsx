#r @"..\Farmer\bin\Debug\netstandard2.0\Farmer.dll"

open Farmer

let myStorage = storageAccount {
    name "mystorage"
    sku Storage.Sku.PremiumLRS
}

let myWebApp = webApp {
    name "mysuperwebapp"
    sku Sku.S1
    app_insights_off
    setting "storage_key" myStorage.Key
    depends_on myStorage
}

let template = arm {
    location NorthEurope
    add_resource myStorage
    add_resource myWebApp
    output "storage_key" myStorage.Key
    output "web_password" myWebApp.PublishingPassword
}

template
|> Writer.quickDeploy "my-resource-group-name"