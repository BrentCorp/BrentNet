const BRENTNET_ROUTES = {
    "home": "https://brentsalmon.com",
    "home.brent": "https://brentsalmon.com",
    "corp": "https://brentcorp.net",
    "corp.brent": "https://brentcorp.net"
};

function cleanInput(input) {
    let value = String(input || "").trim().toLowerCase();

    if (value.startsWith("https://")) {
        value = value.slice(8);
    }

    if (value.startsWith("http://")) {
        value = value.slice(7);
    }

    if (value.startsWith("www.")) {
        value = value.slice(4);
    }

    const slashIndex = value.indexOf("/");
    if (slashIndex !== -1) {
        value = value.slice(0, slashIndex);
    }

    return value;
}

function getDestination(input) {
    const key = cleanInput(input);
    return BRENTNET_ROUTES[key] || null;
}

chrome.runtime.onInstalled.addListener(function () {
    console.log("[BrentNET] Installed.");
});

chrome.omnibox.setDefaultSuggestion({
    description: "BrentNET: type home or corp"
});

chrome.omnibox.onInputChanged.addListener(function (text, suggest) {
    const key = cleanInput(text);
    const destination = getDestination(key);

    suggest([
        {
            content: key,
            description: destination
                ? "Open " + key + " via BrentNET"
                : "Unknown BrentNET address: " + key
        }
    ]);
});

chrome.omnibox.onInputEntered.addListener(function (text) {
    const destination = getDestination(text);

    console.log("[BrentNET] Entered:", text);
    console.log("[BrentNET] Destination:", destination);

    if (destination) {
        chrome.tabs.update({ url: destination });
    }
});