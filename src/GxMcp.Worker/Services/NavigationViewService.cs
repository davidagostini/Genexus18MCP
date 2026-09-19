using System;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Wave-3 item — "View Navigation / View Last Navigation" adapter over <see cref="NavigationService"/>.
    /// </summary>
    public class NavigationViewService
    {
        private readonly NavigationService _navigation;
        private readonly KbService _kbService;

        public NavigationViewService(NavigationService navigation, KbService kbService)
        {
            _navigation = navigation;
            _kbService = kbService;
        }

        public string View(string name, bool latest)
        {
            if (string.IsNullOrWhiteSpace(name))
                return new JObject { ["error"] = "Missing 'name'." }.ToString(Newtonsoft.Json.Formatting.None);

            if (_navigation == null)
            {
                return new JObject { ["error"] = "Navigation returned no payload.", ["code"] = "NoNavigation" }
                    .ToString(Newtonsoft.Json.Formatting.None);
            }

            return _navigation.View(name, latest);
        }
    }
}
