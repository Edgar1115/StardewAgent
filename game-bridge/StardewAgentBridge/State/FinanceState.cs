using System.Collections.Generic;

namespace StardewAgentBridge.State
{
    /// <summary>
    /// Money figures, each with an explicit scope. Current gold, lifetime earnings and shipping value are separate numbers.
    /// </summary>
    public sealed record FinanceState
    {
        /// <summary>Gold in the local player's wallet now (Farmer.Money). Shared or individual per <see cref="WalletScope"/>.</summary>
        public int Money { get; init; }
        /// <summary>"shared" (one team wallet) or "individual" (separate wallets enabled).</summary>
        public string WalletScope { get; init; } = "";
        public bool SeparateWallets { get; init; }

        /// <summary>
        /// Lifetime gold earned by the whole farm (Farmer.totalMoneyEarned, stored on FarmerTeam). Team scope, even with separate wallets.
        /// The game adds every increase of Money to it (shipping, shop sales, rewards).
        /// </summary>
        public long TotalMoneyEarned { get; init; }
        public string TotalMoneyEarnedScope { get; init; } = "team";
        /// <summary>Lifetime gold earned by this player (Stats.IndividualMoneyEarned); null unless separate wallets are on. See <see cref="NullReasons"/>.</summary>
        public long? IndividualMoneyEarned { get; init; }

        /// <summary>Non-empty stacks currently in this player's farm shipping bin (Farm.getShippingBin).</summary>
        public int ShippingBinItemCount { get; init; }
        /// <summary>
        /// Estimated payout of the farm shipping bin if shipped tonight: sum of Object.sellToStorePrice() * Stack, the same formula the game uses overnight.
        /// Excludes Mini-Shipping Bins. An estimate, not income.
        /// </summary>
        public int ShippingBinEstimatedValue { get; init; }
        /// <summary>Gold actually paid for today's shipment. Always null in this version; see <see cref="NullReasons"/>.</summary>
        public int? ShippedIncomeToday { get; init; }

        /// <summary>Why a nullable field is null, keyed by snake_case field name.</summary>
        public IReadOnlyDictionary<string, string> NullReasons { get; init; } = new Dictionary<string, string>();
    }
}
