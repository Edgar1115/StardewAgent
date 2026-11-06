using System.Collections.Generic;
using StardewAgentBridge.State;
using StardewValley;
using StardewValley.Inventories;

namespace StardewAgentBridge.Observers
{
    /// <summary>Reads gold and earnings with explicit scopes. Main thread only.</summary>
    internal static class FinanceObserver
    {
        public static FinanceState Read(Farmer player)
        {
            bool separate = player.team.useSeparateWallets.Value;
            var nullReasons = new Dictionary<string, string>
            {
                ["shipped_income_today"] =
                    "The game pays shipping overnight (Game1._newDayAfterFade) and does not store the paid amount in a public field. "
                    + "Use shipping_bin_estimated_value before sleeping, or the money difference across the night."
            };

            long? individualEarned = null;
            if (separate)
                individualEarned = player.stats.IndividualMoneyEarned;
            else
                nullReasons["individual_money_earned"] =
                    "Separate wallets are off; the game only updates Stats.IndividualMoneyEarned when they are on. Use total_money_earned (team).";

            (int binCount, int binValue) = ReadShippingBin(player);

            return new FinanceState
            {
                Money = player.Money,
                WalletScope = separate ? "individual" : "shared",
                SeparateWallets = separate,
                TotalMoneyEarned = player.totalMoneyEarned,
                TotalMoneyEarnedScope = "team",
                IndividualMoneyEarned = individualEarned,
                ShippingBinItemCount = binCount,
                ShippingBinEstimatedValue = binValue,
                ShippedIncomeToday = null,
                NullReasons = nullReasons
            };
        }

        /// <summary>Same formula as the overnight payout: Object.sellToStorePrice() * Stack, non-Object items count as 0.</summary>
        private static (int count, int value) ReadShippingBin(Farmer player)
        {
            Farm? farm = Game1.getFarm();
            IInventory? bin = farm?.getShippingBin(player);
            if (bin == null)
                return (0, 0);

            int count = 0;
            int value = 0;
            foreach (Item? item in bin)
            {
                if (item == null)
                    continue;
                count++;
                if (item is Object obj)
                    value += obj.sellToStorePrice() * obj.Stack;
            }
            return (count, value);
        }
    }
}
