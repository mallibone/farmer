[<AutoOpen>]
module Farmer.Search

open Farmer

module Sku =
    type HostingMode = Default | HighDensity
    /// The SKU of the search service you want to create. E.g. free or standard.
    type SearchSku =
        | Free
        | Basic
        | Standard
        | Standard2
        | Standard3 of HostingMode
        | StorageOptimisedL1
        | StorageOptimisedL2

type SearchConfig =
    { Name : ResourceName
      Sku : Sku.SearchSku
      Replicas : int
      Partitions : int }
    /// Gets an ARM expression for the admin key of the search instance.
    member this.AdminKey =
        sprintf "[listAdminKeys('Microsoft.Search/searchServices/%s', '2015-08-19').primaryKey]" this.Name.Value
    /// Gets an ARM expression for the query key of the search instance.
    member this.QueryKey =
        sprintf "[listQueryKeys('Microsoft.Search/searchServices/%s', '2015-08-19').value[0].key]" this.Name.Value

type SearchBuilder() =
    member __.Yield _ =
        { Name = ResourceName.Empty
          Sku = Sku.Standard
          Replicas = 1
          Partitions = 1 }        
    member __.Run(state:SearchConfig) =
        { state with Name = state.Name |> sanitiseSearch |> ResourceName }
    [<CustomOperation "name">]
    /// Sets the name of the Azure Search instance.
    member __.Name(state:SearchConfig, name) = { state with Name = name }
    member this.Name(state:SearchConfig, name) = this.Name(state, ResourceName name)
    [<CustomOperation "sku">]
    /// Sets the sku of the Azure Search instance.
    member __.Sku(state:SearchConfig, sku) = { state with Sku = sku }
    [<CustomOperation "replicas">]
    /// Sets the replica count of the Azure Search instance.
    member __.ReplicaCount(state:SearchConfig, replicas:int) = { state with Replicas = replicas }
    [<CustomOperation "partitions">]
    /// Sets the number of partitions of the Azure Search instance.
    member __.PartitionCount(state:SearchConfig, partitions:int) = { state with Partitions = partitions }

open WebApp
type WebAppBuilder with
    member this.DependsOn(state:WebAppConfig, search:SearchConfig) =
        this.DependsOn(state, search.Name)
type FunctionsBuilder with
    member this.DependsOn(state:FunctionsConfig, search:SearchConfig) =
        this.DependsOn(state, search.Name)

module Converters =
   open Farmer.Internal
   let search location (search:SearchConfig) =
        { Name = search.Name
          Location = location
          Sku =
            match search.Sku with
            | Sku.Free -> "free"
            | Sku.Basic -> "basic"
            | Sku.Standard -> "standard"
            | Sku.Standard2 -> "standard2"
            | Sku.Standard3 _ -> "standard3"
            | Sku.StorageOptimisedL1 -> "storage_optimized_l1"
            | Sku.StorageOptimisedL2 -> "storage_optimized_l2"
          ReplicaCount = search.Replicas
          PartitionCount = search.Partitions
          HostingMode = 
            match search.Sku with
            | Sku.Standard3 Sku.HighDensity -> "highDensity"
            | _ -> "default"
          }

let search = SearchBuilder()
