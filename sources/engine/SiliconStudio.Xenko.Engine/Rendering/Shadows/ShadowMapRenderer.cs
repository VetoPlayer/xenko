// Copyright (c) 2014 Silicon Studio Corp. (http://siliconstudio.co.jp)
// This file is distributed under GPL v3. See LICENSE.md for details.

using System;
using System.Collections.Generic;

using SiliconStudio.Core;
using SiliconStudio.Core.Collections;
using SiliconStudio.Core.Extensions;
using SiliconStudio.Core.Mathematics;
using SiliconStudio.Xenko.Engine;
using SiliconStudio.Xenko.Engine.Processors;
using SiliconStudio.Xenko.Graphics;
using SiliconStudio.Xenko.Rendering.Lights;
using SiliconStudio.Xenko.Shaders;

namespace SiliconStudio.Xenko.Rendering.Shadows
{
    /// <summary>
    /// Handles rendering of shadow map casters.
    /// </summary>
    [DataContract(DefaultMemberMode = DataMemberMode.Never)]
    public class ShadowMapRenderer : IShadowMapRenderer
    {
        // TODO: Extract a common interface and implem for shadow renderer (not only shadow maps)
        private readonly int MaximumTextureSize = (int)(ReferenceShadowSize * ComputeSizeFactor(LightShadowMapSize.XLarge) * 2.0f);
        private const float ReferenceShadowSize = 1024;

        private readonly List<RenderStage> shadowMapRenderStages;

        private PoolListStruct<ShadowMapRenderView> shadowRenderViews;

        private FastListStruct<ShadowMapAtlasTexture> atlases;

        public ShadowMapRenderer()
        {
            atlases = new FastListStruct<ShadowMapAtlasTexture>(16);
            shadowRenderViews = new PoolListStruct<ShadowMapRenderView>(16, CreateShadowRenderView);
        }

        /// <summary>
        /// Gets or sets the render view.
        /// </summary>
        /// <value>The render view.</value>
        public RenderView CurrentView { get; private set; }

        [DataMember]
        public List<ILightShadowMapRenderer> Renderers { get; } = new List<ILightShadowMapRenderer>();

        public RenderSystem RenderSystem { get; set; }

        public HashSet<RenderView> RenderViewsWithShadows { get; } = new HashSet<RenderView>();

        // TODO
        public IReadOnlyList<RenderStage> ShadowMapRenderStages => shadowMapRenderStages;

        public ILightShadowMapRenderer FindRenderer(IDirectLight light)
        {
            foreach (var renderer in Renderers)
            {
                if (renderer.CanRenderLight(light))
                    return renderer;
            }

            return null;
        }

        public void Collect(RenderContext context, Dictionary<RenderView, ForwardLightingRenderFeature.RenderViewLightData> renderViewLightDatas)
        {
            // Reset the state of renderers
            foreach (var renderer in Renderers)
            {
                renderer.Reset(context);
            }

            foreach (var renderViewData in renderViewLightDatas)
            {
                renderViewData.Value.LightComponentsWithShadows.Clear();

                // Collect shadows only if enabled on this view
                if (!RenderViewsWithShadows.Contains(renderViewData.Key))
                    continue;

                // Gets the current camera
                CurrentView = renderViewData.Key;

                // Make sure the view is collected (if not done previously)
                context.VisibilityGroup.TryCollect(CurrentView);

                // Check if there is any shadow receivers at all
                if (CurrentView.MinimumDistance >= CurrentView.MaximumDistance)
                {
                    continue;
                }

                // Clear atlases
                foreach (var atlas in atlases)
                {
                    atlas.Clear();
                }

                // Collect all required shadow maps
                CollectShadowMaps(renderViewData.Key, renderViewData.Value);

                foreach (var lightShadowMapTexture in renderViewData.Value.LightComponentsWithShadows)
                {
                    var shadowMapTexture = lightShadowMapTexture.Value;

                    // Could we allocate shadow map? if not, skip
                    if (shadowMapTexture.Atlas == null)
                        continue;

                    // Collect views
                    shadowMapTexture.Renderer.Collect(context, renderViewData.Key, shadowMapTexture);
                }
            }
        }

        public void PrepareAtlasAsRenderTargets(CommandList commandList)
        {
            // Clear atlases
            foreach (var atlas in atlases)
            {
                atlas.PrepareAsRenderTarget(commandList);
            }
        }

        public void PrepareAtlasAsShaderResourceViews(CommandList commandList)
        {
            foreach (var atlas in atlases)
            {
                atlas.PrepareAsShaderResourceView(commandList);
            }
        }

        public void Flush(RenderDrawContext context)
        {
            RenderViewsWithShadows.Clear();
        }

        private void AssignRectangle(LightShadowMapTexture lightShadowMapTexture)
        {
            var size = lightShadowMapTexture.Size;

            // Try to fit the shadow map into an existing atlas
            ShadowMapAtlasTexture currentAtlas = null;
            foreach (var atlas in atlases)
            {
                if (atlas.TryInsert(size, size, lightShadowMapTexture.CascadeCount, (int index, ref Rectangle rectangle) => lightShadowMapTexture.SetRectangle(index, rectangle)))
                {
                    currentAtlas = atlas;
                    break;
                }
            }

            // Allocate a new atlas texture
            if (currentAtlas == null)
            {
                // For now, our policy is to allow only one shadow map, esp. because we can have only one shadow texture per lighting group
                // TODO: Group by DirectLightGroups, so that we can have different atlas per lighting group
                // TODO: Allow multiple textures per LightingGroup (using array of Texture?)
                if (atlases.Count == 0)
                {
                    // TODO: handle FilterType texture creation here
                    // TODO: This does not work for Omni lights
                    // TODO: Allow format selection externally

                    var texture = Texture.New2D(RenderSystem.GraphicsDevice, MaximumTextureSize, MaximumTextureSize, 1, PixelFormat.D32_Float, TextureFlags.DepthStencil | TextureFlags.ShaderResource);
                    currentAtlas = new ShadowMapAtlasTexture(texture, atlases.Count) { FilterType = lightShadowMapTexture.FilterType };
                    atlases.Add(currentAtlas);

                    for (int i = 0; i < lightShadowMapTexture.CascadeCount; i++)
                    {
                        var rect = Rectangle.Empty;
                        currentAtlas.Insert(size, size, ref rect);
                        lightShadowMapTexture.SetRectangle(i, rect);
                    }
                }
            }

            // Make sure the atlas cleared (will be clear just once)
            lightShadowMapTexture.Atlas = currentAtlas;
            lightShadowMapTexture.TextureId = (byte)(currentAtlas?.Id ?? 0);
        }

        private void CollectShadowMaps(RenderView renderView, ForwardLightingRenderFeature.RenderViewLightData renderViewLightData)
        {
            // TODO GRAPHICS REFACTOR Only lights of current scene!
            foreach (var lightComponent in renderViewLightData.VisibleLightsWithShadows)
            {
                var light = lightComponent.Type as IDirectLight;
                if (light == null)
                {
                    continue;
                }

                var shadowMap = light.Shadow;
                if (!shadowMap.Enabled)
                {
                    continue;
                }

                // Check if the light has a shadow map renderer
                var renderer = FindRenderer(light);
                if (renderer == null)
                {
                    continue;
                }

                var direction = lightComponent.Direction;
                var position = lightComponent.Position;

                // Compute the coverage of this light on the screen
                var size = light.ComputeScreenCoverage(renderView, position, direction);

                // Converts the importance into a shadow size factor
                var sizeFactor = ComputeSizeFactor(shadowMap.Size);

                // Compute the size of the final shadow map
                // TODO: Handle GraphicsProfile
                var shadowMapSize = (int)Math.Min(ReferenceShadowSize * sizeFactor, MathUtil.NextPowerOfTwo(size * sizeFactor));

                if (shadowMapSize <= 0) // TODO: Validate < 0 earlier in the setters
                {
                    continue;
                }

                var shadowMapTexture = renderer.CreateShadowMapTexture(lightComponent, light, shadowMapSize);

                // Assign rectangles for shadowmap
                AssignRectangle(shadowMapTexture);

                renderViewLightData.LightComponentsWithShadows.Add(lightComponent, shadowMapTexture);
            }
        }

        private static float ComputeSizeFactor(LightShadowMapSize shadowMapSize)
        {
            // Then reduce the size based on the shadow map size
            var factor = (float)Math.Pow(2.0f, (int)shadowMapSize - 3.0f);
            return factor;
        }

        private static LightShadowMapTexture CreateLightShadowMapTexture()
        {
            return new LightShadowMapTexture();
        }
        
        private static ShadowMapRenderView CreateShadowRenderView()
        {
            return new ShadowMapRenderView();
        }

        public struct LightComponentKey : IEquatable<LightComponentKey>
        {
            public readonly LightComponent LightComponent;

            public readonly RenderView RenderView;

            public LightComponentKey(LightComponent lightComponent, RenderView renderView)
            {
                LightComponent = lightComponent;
                RenderView = renderView;
            }

            public bool Equals(LightComponentKey other)
            {
                return LightComponent == other.LightComponent && RenderView == other.RenderView;
            }

            public override int GetHashCode()
            {
                return LightComponent.GetHashCode() ^ (397 * RenderView.GetHashCode());
            }
        }
    }
}