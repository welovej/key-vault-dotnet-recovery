using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.KeyVault;
using Azure.ResourceManager.KeyVault.Models;
using Azure.ResourceManager.Resources;
using Newtonsoft.Json.Linq;
using System;
using System.Configuration;
using System.Net;
using System.Net.Http;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AzureKeyVaultRecoverySamples
{
    /// <summary>
    /// Contains samples illustrating enabling recoverable deletion for Azure key vaults,
    /// as well as exercising the recovery and purge functionality, respectively.
    /// </summary>
    public sealed class KeyVaultRecoverySamples
    {
        /// <summary>
        /// Builds a vault recovery sample object with the specified parameters.
        /// </summary>
        /// <param name="tenantId">Tenant id.</param>
        /// <param name="objectId">Object id of the Service Principal used to run the sample.</param>
        /// <param name="appId">AD application id.</param>
        /// <param name="appCredX5T">Thumbprint of the certificate set as the credential for the AD application.</param>
        /// <param name="subscriptionId">Subscription id.</param>
        /// <param name="resourceGroupName">Resource group name.</param>
        /// <param name="vaultLocation">Location of the vault.</param>
        /// <param name="vaultName">Vault name.</param>
        public KeyVaultRecoverySamples(string tenantId, string objectId, string appId, string appCredX5T, string subscriptionId, string resourceGroupName, string vaultLocation, string vaultName)
        { }

        /// <summary>
        /// Builds a vault recovery sample object from configuration.
        /// </summary>
        public KeyVaultRecoverySamples()
        { }
        // retrieve parameters from configuration
        static string rgName = ConfigurationManager.AppSettings[SampleConstants.ConfigKeys.ResourceGroupName];
        static string vaultName = ConfigurationManager.AppSettings[SampleConstants.ConfigKeys.VaultName];

        /// <summary>
        /// Verifies the specified exception is a CloudException, and its status code matches the expected value.
        /// </summary>
        /// <param name="e"></param>
        /// <param name="expectedStatusCode"></param>
        protected static void VerifyExpectedARMException(Exception e, HttpStatusCode expectedStatusCode)
        {
            // verify that the exception is a CloudError one
            var armException = e as Azure.RequestFailedException;
            if (armException == null)
            {
                Console.WriteLine("Unexpected exception encountered running sample: {0}", e.Message);
                throw e;
            }

            // verify that the exception has the expected status code
            if (armException.Status != (int)expectedStatusCode)
            {
                Console.WriteLine("Encountered unexpected ARM exception; expected status code: {0}, actual: {1}", armException.Status, expectedStatusCode);
                throw e;
            }
        }
        #region samples
        /// <summary>
        /// Demonstrates how to enable soft delete on an existing vault, and then proceeds to delete, recover and purge the vault.
        /// Assumes the caller has the KeyVaultContributor role in the subscription.
        /// </summary>
        /// <returns>Task representing this functionality.</returns>
        public static async Task DemonstrateRecoveryAndPurgeForNewVaultAsync()
        {
            var cred = new DefaultAzureCredential();
            var client = new ArmClient(cred);
            var sub = (await client.GetDefaultSubscriptionAsync());
            var location = AzureLocation.EastUS;
            var rg = (await sub.GetResourceGroups().CreateOrUpdateAsync(WaitUntil.Completed, rgName, new ResourceGroupData(location))).Value;
            try
            {
                var keyVaultSku = new KeyVaultSku(KeyVaultSkuFamily.A, KeyVaultSkuName.Standard);
                var keyVaultProperties = new KeyVaultProperties(Guid.NewGuid(), keyVaultSku) { EnableSoftDelete = true };

                var content = new KeyVaultCreateOrUpdateContent(AzureLocation.EastUS, keyVaultProperties);

                Console.WriteLine("Operating with vault name '{0}' in resource group '{1}' and location '{2}'", vaultName, rgName, content.Location);

                // create new soft-delete-enabled vault
                Console.Write("Creating vault...");
                var keyVaultResource = (await rg.GetKeyVaults().CreateOrUpdateAsync(WaitUntil.Completed, vaultName, content)).Value;
                Console.WriteLine("done.");

                // wait for the DNS record to propagate; verify properties
                Console.Write("Waiting for DNS propagation..");
                Thread.Sleep(10 * 1000);
                Console.WriteLine("done.");

                Console.Write("Retrieving newly created vault...");
                var retrievedVault = (await rg.GetKeyVaultAsync(vaultName)).Value;
                Console.WriteLine("done.");

                // delete vault
                Console.Write("Deleting vault...");
                await retrievedVault.DeleteAsync(WaitUntil.Completed);
                Console.WriteLine("done.");

                // confirm the existence of the deleted vault
                Console.Write("Retrieving deleted vault...");
                ResourceIdentifier rid = DeletedKeyVaultResource.CreateResourceIdentifier(sub.Data.SubscriptionId, location, vaultName);
                DeletedKeyVaultResource deletedRes = await client.GetDeletedKeyVaultResource(rid).GetAsync();
                Console.WriteLine("done; '{0}' deleted on: {1}, scheduled for purge on: {2}", deletedRes.Data.Id, deletedRes.Data.Properties.DeletedOn, deletedRes.Data.Properties.ScheduledPurgeOn);

                // recover; set the creation mode as 'recovery' in the vault parameters
                Console.Write("Recovering deleted vault...");
                var keyVaultSku_recover = new KeyVaultSku(KeyVaultSkuFamily.A, KeyVaultSkuName.Standard);
                var keyVaultProperties_recover = new KeyVaultProperties(Guid.NewGuid(), keyVaultSku) { CreateMode = KeyVaultCreateMode.Recover };
                var content_recover = new KeyVaultCreateOrUpdateContent(AzureLocation.EastUS, keyVaultProperties_recover);
                await rg.GetKeyVaults().CreateOrUpdateAsync(WaitUntil.Completed, vaultName, content_recover);
                Console.WriteLine("done.");

                // confirm recovery
                Console.Write("Verifying the existence of recovered vault...");
                var recoveredVault_identifier = KeyVaultResource.CreateResourceIdentifier(sub.Data.SubscriptionId, rgName, vaultName);
                await client.GetKeyVaultResource(recoveredVault_identifier).GetAsync();
                Console.WriteLine("done.");

                // delete vault
                Console.Write("Deleting vault...");
                await retrievedVault.DeleteAsync(WaitUntil.Completed);
                Console.WriteLine("done.");

                // purge vault
                Console.Write("Purging vault...");
                ResourceIdentifier rid_purge = DeletedKeyVaultResource.CreateResourceIdentifier(sub.Data.SubscriptionId, location, vaultName);
                DeletedKeyVaultResource deletedRes_purge = await client.GetDeletedKeyVaultResource(rid_purge).GetAsync();
                await deletedRes_purge.PurgeDeletedAsync(WaitUntil.Completed);
                Console.WriteLine("done.");
            }
            catch (Exception e)
            {
                Console.WriteLine("unexpected exception encountered running the test: {message}", e.Message);
                throw;
            }

            // verify purge
            try
            {
                Console.Write("Verifying vault deletion succeeded...");
                var recoveredVault_identifier = KeyVaultResource.CreateResourceIdentifier(sub.Data.SubscriptionId, rgName, vaultName);
                var keyValt = (await client.GetKeyVaultResource(recoveredVault_identifier).GetAsync()).Value;
                var data = keyValt.Data;
            }
            catch (Exception e)
            {
                // no op; expected
                VerifyExpectedARMException(e, HttpStatusCode.NotFound);
                Console.WriteLine("done.");
            }

            try
            {
                Console.Write("Verifying vault purging succeeded...");
                ResourceIdentifier rid = DeletedKeyVaultResource.CreateResourceIdentifier(sub.Data.SubscriptionId, location, vaultName);
                DeletedKeyVaultResource deletedRes = await client.GetDeletedKeyVaultResource(rid).GetAsync();
                var data = deletedRes.Data;

            }
            catch (Exception e)
            {
                // no op; expected
                VerifyExpectedARMException(e, HttpStatusCode.NotFound);
                Console.WriteLine("done.");
            }
        }

        /// <summary>
        /// Demonstrates how to enable soft delete on an existing vault, and then proceeds to delete, recover and purge the vault.
        /// Assumes the caller has the KeyVaultContributor role in the subscription.
        /// </summary>
        /// <returns>Task representing this functionality.</returns>
        public static async Task DemonstrateRecoveryAndPurgeForExistingVaultAsync()
        {
            var cred = new DefaultAzureCredential();
            var client = new ArmClient(cred);
            var sub = (await client.GetDefaultSubscriptionAsync());
            var location = AzureLocation.EastUS;
            ResourceGroupResource rg = (await sub.GetResourceGroups().CreateOrUpdateAsync(WaitUntil.Completed, rgName, new ResourceGroupData(location))).Value;
            try
            {
                var keyVaultSku = new KeyVaultSku(KeyVaultSkuFamily.A, KeyVaultSkuName.Standard);
                var keyVaultProperties = new KeyVaultProperties(Guid.NewGuid(), keyVaultSku) { EnableSoftDelete = false };
                var content = new KeyVaultCreateOrUpdateContent(AzureLocation.EastUS, keyVaultProperties);

                Console.WriteLine("Operating with vault name '{0}' in resource group '{1}' and location '{2}'", vaultName, rgName, content.Location);

                // create new vault, not enabled for soft delete
                Console.Write("Creating vault...");
                var keyVaultResource = (await rg.GetKeyVaults().CreateOrUpdateAsync(WaitUntil.Completed, vaultName, content)).Value;
                Console.WriteLine("done.");

                // wait for the DNS record to propagate; verify properties
                Console.Write("Waiting for DNS propagation..");
                Thread.Sleep(10 * 1000);
                Console.WriteLine("done.");

                Console.Write("Retrieving newly created vault...");
                var retrievedVault = (await rg.GetKeyVaultAsync(vaultName)).Value;
                Console.WriteLine("done.");

                // enable soft delete on existing vault
                Console.Write("Enabling soft delete on existing vault...");
                retrievedVault.Data.Properties.EnableSoftDelete = true;
                Console.WriteLine("done.");

                // delete vault
                Console.Write("Deleting vault...");
                await retrievedVault.DeleteAsync(WaitUntil.Completed);
                Console.WriteLine("done.");

                // confirm the existence of the deleted vault
                Console.Write("Retrieving deleted vault...");
                ResourceIdentifier rid = DeletedKeyVaultResource.CreateResourceIdentifier(sub.Data.SubscriptionId, location, vaultName);
                DeletedKeyVaultResource deletedRes = await client.GetDeletedKeyVaultResource(rid).GetAsync();
                Console.WriteLine("done; '{0}' deleted on: {1}, scheduled for purge on: {2}", deletedRes.Data.Id, deletedRes.Data.Properties.DeletedOn, deletedRes.Data.Properties.ScheduledPurgeOn);

                // recover; set the creation mode as 'recovery' in the vault parameters
                Console.Write("Recovering deleted vault...");
                var keyVaultSku_recover = new KeyVaultSku(KeyVaultSkuFamily.A, KeyVaultSkuName.Standard);
                var keyVaultProperties_recover = new KeyVaultProperties(Guid.NewGuid(), keyVaultSku) { CreateMode = KeyVaultCreateMode.Recover };
                var content_recover = new KeyVaultCreateOrUpdateContent(AzureLocation.EastUS, keyVaultProperties_recover);
                await rg.GetKeyVaults().CreateOrUpdateAsync(WaitUntil.Completed, vaultName, content_recover);
                Console.WriteLine("done.");

                // confirm recovery
                Console.Write("Verifying the existence of recovered vault...");
                var recoveredVault_identifier = KeyVaultResource.CreateResourceIdentifier(sub.Data.SubscriptionId, rgName, vaultName);
                await client.GetKeyVaultResource(recoveredVault_identifier).GetAsync();
                Console.WriteLine("done.");

                // delete vault
                Console.Write("Deleting vault...");
                await retrievedVault.DeleteAsync(WaitUntil.Completed);
                Console.WriteLine("done.");

                Console.Write("Purging vault...");
                ResourceIdentifier rid_purge = DeletedKeyVaultResource.CreateResourceIdentifier(sub.Data.SubscriptionId, location, vaultName);
                DeletedKeyVaultResource deletedRes_purge = (await client.GetDeletedKeyVaultResource(rid_purge).GetAsync()).Value;
                await deletedRes_purge.PurgeDeletedAsync(WaitUntil.Completed);
                Console.WriteLine("done.");
            }
            catch (Exception e)
            {
                Console.WriteLine("unexpected exception encountered running the test: {0}", e.Message);
                throw;
            }

            // verify purge
            try
            {
                Console.Write("Verifying vault deletion succeeded...");
                var recoveredVault_identifier = KeyVaultResource.CreateResourceIdentifier(sub.Data.SubscriptionId, rgName, vaultName);
                var keyValt = (await client.GetKeyVaultResource(recoveredVault_identifier).GetAsync()).Value;
                var data = keyValt.Data;
            }
            catch (Exception e)
            {
                // no op; expected
                VerifyExpectedARMException(e, HttpStatusCode.NotFound);
                Console.WriteLine("done.");
            }

            try
            {
                Console.Write("Verifying vault purging succeeded...");
                ResourceIdentifier rid = DeletedKeyVaultResource.CreateResourceIdentifier(sub.Data.SubscriptionId, location, vaultName);
                DeletedKeyVaultResource deletedRes = (await client.GetDeletedKeyVaultResource(rid).GetAsync()).Value;
                var data = deletedRes.Data;
            }
            catch (Exception e)
            {
                // no op; expected
                VerifyExpectedARMException(e, HttpStatusCode.NotFound);
                Console.WriteLine("done.");
            }
        }
        #endregion
    }
}
