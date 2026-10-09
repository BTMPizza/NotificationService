using System.Text.Json.Nodes;

namespace BtmPizza.Notifications;

/// <summary>Sample content for test sends, one per notification type.</summary>
public static class Samples
{
    public static JsonObject For(string type) => (JsonObject)All[type]!.DeepClone();

    public static readonly JsonObject All = new()
    {
        ["OFFER"] = new JsonObject
        {
            ["type"] = "OFFER",
            ["title"] = "Two mediums for ₹599",
            ["body"] = "Today only at BTM Layout · earns double Dough Coins.",
            ["kicker"] = "Weekend offer",
            ["article"] = new JsonArray(
                "Grab any two medium pizzas for ₹599 at our BTM Layout outlet, today only.",
                "Every order also earns double Dough Coins."),
        },
        ["WALLET_UPDATE"] = new JsonObject
        {
            ["type"] = "WALLET_UPDATE",
            ["title"] = "You earned 120 Dough Coins",
            ["body"] = "Your balance is now 860 coins, 140 away from Gold tier.",
            ["kicker"] = "Coins earned",
            ["article"] = new JsonArray(
                "Thanks for your order! 120 Dough Coins have been added to your wallet.",
                "Reach 1,000 coins to unlock Gold tier."),
        },
        ["PIZZA_NEWS"] = new JsonObject
        {
            ["type"] = "PIZZA_NEWS",
            ["title"] = "New: Peri Peri Paneer pizza",
            ["body"] = "Our spiciest pizza yet is now on the menu at every outlet.",
            ["kicker"] = "New on the menu",
            ["article"] = new JsonArray(
                "Smoky peri peri paneer, roasted peppers and onions on our hand-tossed base.",
                "Available now for dine-in and delivery."),
        },
        ["CUSTOMER_REFERRAL"] = new JsonObject
        {
            ["type"] = "CUSTOMER_REFERRAL",
            ["title"] = "Your friend just joined BTM Pizza",
            ["body"] = "Riya used your referral code · 200 Dough Coins are on their way.",
            ["kicker"] = "Referral reward",
            ["article"] = new JsonArray(
                "Riya signed up with your referral code and placed their first order.",
                "200 Dough Coins have been added to your wallet. Keep sharing your code to earn more."),
        },
    };
}
