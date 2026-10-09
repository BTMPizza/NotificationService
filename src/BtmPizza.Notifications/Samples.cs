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
        ["COINS_EARNED"] = new JsonObject
        {
            ["type"] = "COINS_EARNED",
            ["title"] = "You earned 120 Dough Coins",
            ["body"] = "Your balance is now 860 coins, 140 away from Gold tier.",
            ["kicker"] = "Coins earned",
            ["article"] = new JsonArray(
                "Thanks for your order! 120 Dough Coins have been added to your wallet.",
                "Reach 1,000 coins to unlock Gold tier."),
        },
        ["COINS_REDEEMED"] = new JsonObject
        {
            ["type"] = "COINS_REDEEMED",
            ["title"] = "You redeemed 300 Dough Coins",
            ["body"] = "₹150 off your order · balance is now 560 coins.",
            ["kicker"] = "Coins redeemed",
            ["article"] = new JsonArray(
                "You used 300 Dough Coins for ₹150 off your order.",
                "Your remaining balance is 560 coins."),
        },
        ["TIER_CHANGED"] = new JsonObject
        {
            ["type"] = "TIER_CHANGED",
            ["title"] = "Welcome to Gold tier",
            ["body"] = "You crossed 1,000 Dough Coins · enjoy 1.5x coins on every order.",
            ["kicker"] = "Tier upgrade",
            ["article"] = new JsonArray(
                "Congratulations! You are now a Gold member.",
                "Gold members earn 1.5x Dough Coins on every order and get early access to new pizzas."),
        },
        ["CUSTOMER_REGISTERED"] = new JsonObject
        {
            ["type"] = "CUSTOMER_REGISTERED",
            ["title"] = "Welcome to BTM Pizza",
            ["body"] = "Your account is ready · 100 Dough Coins are waiting in your wallet.",
            ["kicker"] = "Welcome",
            ["article"] = new JsonArray(
                "Thanks for joining BTM Pizza! We've added 100 Dough Coins to your wallet to get you started.",
                "Share your referral code with friends to earn more."),
        },
        ["REFERRAL_COMPLETED"] = new JsonObject
        {
            ["type"] = "REFERRAL_COMPLETED",
            ["title"] = "Your friend just joined BTM Pizza",
            ["body"] = "Riya used your referral code · 200 Dough Coins are on their way.",
            ["kicker"] = "Referral reward",
            ["article"] = new JsonArray(
                "Riya signed up with your referral code and placed their first order.",
                "200 Dough Coins have been added to your wallet. Keep sharing your code to earn more."),
        },
    };
}
