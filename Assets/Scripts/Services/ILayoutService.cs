﻿using UnityEngine;

namespace Nofun.Services
{
    public interface ILayoutService
    {
        public Canvas Canvas { get; }

        public void SetVisibility(bool isVisible);

        public void BlockInterfaceInteraction();
        public void UnblockInterfaceInteraction();
    }
}
