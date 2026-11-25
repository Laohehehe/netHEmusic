/* Shared liquid-glass rendering engine. */
(() => {
        const SVG_NS = 'http://www.w3.org/2000/svg';
        const XLINK_NS = 'http://www.w3.org/1999/xlink';
        let svgContainer = null;
        // Keep generated SVG filters bounded.  Each entry owns a data-URL image,
        // so retaining every historical slider value steadily grows page memory.
        const filterCache = new Map();
        const MAX_FILTER_CACHE = 96;
        let filterIdCounter = 0;
        let currentRefractionStrength = 1.2;
        let currentDistortion = 0.6;

        function ensureSvgContainer() {
            if (svgContainer && document.body.contains(svgContainer)) return svgContainer;
            const svg = document.createElementNS(SVG_NS, 'svg');
            svg.setAttribute('xmlns', SVG_NS);
            svg.setAttribute('width', '0');
            svg.setAttribute('height', '0');
            svg.style.cssText = 'position:fixed;top:0;left:0;width:0;height:0;pointer-events:none;overflow:hidden;';
            const defs = document.createElementNS(SVG_NS, 'defs');
            svg.appendChild(defs);
            document.body.appendChild(svg);
            svgContainer = svg;
            return svg;
        }

        function smoothStep(a, b, t) {
            const v = Math.max(0, Math.min(1, (t - a) / (b - a)));
            return v * v * (3 - 2 * v);
        }

        function roundedRectSDF(x, y, halfW, halfH, r) {
            const qx = Math.abs(x) - halfW + r;
            const qy = Math.abs(y) - halfH + r;
            return Math.min(Math.max(qx, qy), 0) + Math.hypot(Math.max(qx, 0), Math.max(qy, 0)) - r;
        }

        function buildDisplacementMap(width, height, radius, strength, distortion, clamp, profile) {
            // clamp: { left, top, viewportWidth, viewportHeight } 元素相对视口的屏幕位置，
            // 用于把折射取样点钳制在网页范围内；null 表示无需限制
            const longSide = Math.max(width, height);
            const halfW = width / longSide / 2;
            const halfH = height / longSide / 2;
            const r = Math.min(radius / longSide, Math.min(halfW, halfH) - 0.001);
            const refractionBand = 0.12;
            // 自适应 Band 裁切：边缘过渡带不超过该轴最小半长的一半，
            // 避免细长元素（如音量条）上过渡带超出自身一半
            // A tiny slider needs a wider lens zone: a narrow edge-only band
            // barely intersects the horizontal rail underneath its centre.
            const effectiveBand = profile === 'slider'
                ? Math.min(halfW, halfH) * 0.95
                : Math.min(refractionBand, Math.min(halfW, halfH) / 2);
            const refractionStrength = strength * 0.05;

            const distortionScale = distortion * 0.35;

            const canvas = document.createElement('canvas');
            canvas.width = Math.round(width);
            canvas.height = Math.round(height);
            const ctx = canvas.getContext('2d');
            if (!ctx) return { url: '', scale: 0 };

            const image = ctx.createImageData(canvas.width, canvas.height);
            const data = image.data;

            let maxScale = 0;
            const buf = new Float32Array(canvas.width * canvas.height * 2);
            let bi = 0;

            for (let y = 0; y < canvas.height; y++) {
                const sampleOffset = profile === 'slider' ? 0.5 : 0;
                const v = ((y + sampleOffset) / canvas.height - 0.5) * (height / longSide);
                for (let x = 0; x < canvas.width; x++) {
                    const u = ((x + sampleOffset) / canvas.width - 0.5) * (width / longSide);
                    const dist = roundedRectSDF(u, v, halfW, halfH, r);
                    const t = smoothStep(-effectiveBand, 0, dist);
                    const len = Math.hypot(u, v) || 1;
                    const baseMag = t * refractionStrength * longSide;
                    // 方向反转：折射取样方向取径向反方向
                    let dx = -(u / len) * baseMag;
                    let dy = -(v / len) * baseMag;

                    if (profile === 'slider') {
                        // A horizontal capsule refracts at its left/right caps.
                        // Radial displacement emphasises its closest edges (top
                        // and bottom), which made the rail look shifted down.
                        const capStart = Math.max(0, halfW - halfH * 1.3);
                        const sideWeight = smoothStep(capStart, halfW, Math.abs(u));
                        const magnitude = Math.min(refractionStrength * longSide, height * 0.4);
                        const flow = 1 + distortionScale * 0.15 * Math.cos(v / halfH * Math.PI);
                        dx = -Math.sign(u) * sideWeight * magnitude * flow;
                        dy = 0;
                    } else if (t > 0 && distortionScale > 0) {
                        const angle = Math.atan2(v, u);
                        const radius = Math.hypot(u, v);
                        const swirl = Math.sin(radius * 10 - angle * 4) * distortionScale * t;
                        const flowX = Math.sin(x * 0.05 + y * 0.03) +
                                      Math.cos(x * 0.02 - y * 0.04) * 0.7;
                        const flowY = Math.sin(y * 0.04 + x * 0.025) +
                                      Math.cos(y * 0.055 - x * 0.018) * 0.7;

                        const tangent = { x: -Math.sin(angle), y: Math.cos(angle) };
                        dx += (tangent.x * swirl + flowX * distortionScale * t);
                        dy += (tangent.y * swirl + flowY * distortionScale * t);
                    }

                    // 限制折射取样范围：把位移后的取样点钳制在网页（可见视口）范围内，
                    // 贴边元素（顶栏/浮动面板）不会把视口外的浏览器 UI 折射进来
                    if (clamp) {
                        const sx = clamp.left + x + dx;
                        const sy = clamp.top + y + dy;
                        dx = Math.min(Math.max(sx, clamp.viewportLeft), clamp.viewportLeft + clamp.viewportWidth) - (clamp.left + x);
                        dy = Math.min(Math.max(sy, clamp.viewportTop), clamp.viewportTop + clamp.viewportHeight) - (clamp.top + y);
                    }

                    maxScale = Math.max(maxScale, Math.abs(dx), Math.abs(dy));
                    buf[bi++] = dx;
                    buf[bi++] = dy;
                }
            }

            if (maxScale === 0) maxScale = 1;

            let i = 0;
            for (let p = 0; p < data.length; p += 4) {
                const r = buf[i++] / maxScale / 2 + 0.5;
                const g = buf[i++] / maxScale / 2 + 0.5;
                data[p] = r * 255;
                data[p + 1] = g * 255;
                data[p + 2] = 0;
                data[p + 3] = 255;
            }

            ctx.putImageData(image, 0, 0);
            return { url: canvas.toDataURL(), scale: maxScale * 2 };
        }

        function getOrCreateFilter(width, height, radius, strength, distortion, clamp, profile = 'edge') {
            const clampKey = clamp
                ? `${Math.round(clamp.left)}-${Math.round(clamp.top)}-${Math.round(clamp.viewportLeft)}-${Math.round(clamp.viewportTop)}-${Math.round(clamp.viewportWidth)}-${Math.round(clamp.viewportHeight)}`
                : 'free';
            const key = `${profile}-${width}-${height}-${radius}-${strength.toFixed(3)}-${distortion.toFixed(3)}-${clampKey}`;
            if (filterCache.has(key)) {
                const cachedId = filterCache.get(key);
                // Map insertion order is our LRU order.
                filterCache.delete(key);
                filterCache.set(key, cachedId);
                return cachedId;
            }

            const svg = ensureSvgContainer();
            const defs = svg.querySelector('defs');
            const id = `lg-edge-${++filterIdCounter}`;

            const filter = document.createElementNS(SVG_NS, 'filter');
            filter.setAttribute('id', id);
            filter.setAttribute('filterUnits', 'userSpaceOnUse');
            filter.setAttribute('x', '0');
            filter.setAttribute('y', '0');
            filter.setAttribute('width', String(width));
            filter.setAttribute('height', String(height));
            filter.setAttribute('colorInterpolationFilters', 'sRGB');

            const feImage = document.createElementNS(SVG_NS, 'feImage');
            feImage.setAttributeNS(XLINK_NS, 'href', '');
            feImage.setAttribute('href', '');
            feImage.setAttribute('x', '0');
            feImage.setAttribute('y', '0');
            feImage.setAttribute('width', String(width));
            feImage.setAttribute('height', String(height));
            feImage.setAttribute('preserveAspectRatio', 'none');
            feImage.setAttribute('result', 'dispMap');

            const feDisp = document.createElementNS(SVG_NS, 'feDisplacementMap');
            feDisp.setAttribute('in', 'SourceGraphic');
            feDisp.setAttribute('in2', 'dispMap');
            feDisp.setAttribute('xChannelSelector', 'R');
            feDisp.setAttribute('yChannelSelector', 'G');
            feDisp.setAttribute('scale', '0');

            filter.appendChild(feImage);
            filter.appendChild(feDisp);
            defs.appendChild(filter);

            const map = buildDisplacementMap(width, height, radius, strength, distortion, clamp, profile);
            feImage.setAttributeNS(XLINK_NS, 'href', map.url);
            feImage.setAttribute('href', map.url);
            feDisp.setAttribute('scale', String(map.scale));

            filterCache.set(key, id);
            return id;
        }

        function pruneFilterCache() {
            if (filterCache.size <= MAX_FILTER_CACHE) return;

            const inUse = new Set();
            document.querySelectorAll('*').forEach(el => {
                const filter = el.style.getPropertyValue('--lg-filter');
                const match = /url\(#([^)]*)\)/.exec(filter);
                if (match) inUse.add(match[1]);
            });

            for (const [key, id] of filterCache) {
                if (filterCache.size <= MAX_FILTER_CACHE) break;
                if (inUse.has(id)) continue;
                document.getElementById(id)?.remove();
                filterCache.delete(key);
            }
        }

        // 计算元素是否需要限制折射取样范围，以及可见网页区域（rect 坐标系下）：
        //  - 浏览器放大 / 捏合缩放时（visualViewport.scale > 1），可见区域是视觉视口：
        //      fixed 元素（顶栏/浮动面板）的 rect 相对视觉视口，可见范围即 [0, vv.width]×[0, vv.height]；
        //      普通元素 rect 相对布局视口，可见范围从 vv.offsetLeft/offsetTop（×scale 还原为布局像素）开始
        //  - 未放大时，可见范围即整个视口 [0, innerWidth]×[0, innerHeight]
        // 其余远离边缘的元素返回 null（沿用无钳制贴图，保持滤镜缓存稳定）
        function getClampInfo(el, rect) {
            const vv = window.visualViewport;
            let vLeft, vTop, vW, vH;
            if (vv && vv.scale > 1.001) {
                if (getComputedStyle(el).position === 'fixed') {
                    vLeft = 0;
                    vTop = 0;
                    vW = vv.width;
                    vH = vv.height;
                } else {
                    vLeft = vv.offsetLeft * vv.scale;
                    vTop = vv.offsetTop * vv.scale;
                    vW = vv.width * vv.scale;
                    vH = vv.height * vv.scale;
                }
            } else {
                vLeft = 0;
                vTop = 0;
                vW = window.innerWidth;
                vH = window.innerHeight;
            }
            const longSide = Math.max(rect.width, rect.height);
            // 位移贴图最大取样偏移 ≈ 折射强度 * 0.05 * 长边 + 液化噪声，留 1.5 倍余量
            const margin = 4 + currentRefractionStrength * 0.05 * longSide * 1.5 + currentDistortion;
            if (rect.left - vLeft > margin && rect.right < vLeft + vW - margin &&
                rect.top - vTop > margin && rect.bottom < vTop + vH - margin) {
                return null;
            }
            return {
                left: rect.left, top: rect.top,
                viewportLeft: vLeft, viewportTop: vTop,
                viewportWidth: vW, viewportHeight: vH
            };
        }

        function applyLiquidGlassEffect(el, enable, rectScale) {
            if (window.SmallLiquidGlass?.isControl(el)) {
                window.SmallLiquidGlass.apply(el, enable);
                return;
            }
            // 通用液态玻璃效果开关，支持任何元素；
            // rectScale 用于元素处于放大动画时，按目标尺寸（当前尺寸 × 倍数）调用着色器生成位移贴图
            if (enable) {
                const rect = el.getBoundingClientRect();
                const width = Math.round(rect.width * (rectScale || 1));
                const height = Math.round(rect.height * (rectScale || 1));
                if (width < 8 || height < 8) return;
                const style = getComputedStyle(el);
                const radius = parseFloat(style.borderTopLeftRadius) || 16;
                const profile = el.dataset.glassProfile;
                const strength = currentRefractionStrength * (profile === 'slider' ? 4 : 1);
                const filterId = getOrCreateFilter(width, height, radius, strength, currentDistortion, getClampInfo(el, rect), profile);
                el.style.setProperty('--lg-filter', `url(#${filterId})`);
                el.classList.add('glass-active');
            } else {
                el.classList.remove('glass-active');
                el.style.removeProperty('--lg-filter');
                pruneFilterCache();
            }
        }

        function updateElementFilter(el) {
            if (el.dataset.morphing) return;
            if (window.SmallLiquidGlass?.isControl(el)) {
                window.SmallLiquidGlass.update(el);
                return;
            }
            if (el.dataset.glassProfile === 'slider' && !el.classList.contains('glass-active')) return;
            const rect = el.getBoundingClientRect();
            const width = Math.round(rect.width);
            const height = Math.round(rect.height);
            if (width < 8 || height < 8) return;

            const style = getComputedStyle(el);
            const radius = parseFloat(style.borderTopLeftRadius) || 16;
            const profile = el.dataset.glassProfile;
            const strength = currentRefractionStrength * (profile === 'slider' ? 4 : 1);
            const distortion = currentDistortion;

            const filterId = getOrCreateFilter(width, height, radius, strength, distortion, getClampInfo(el, rect), profile);
            el.style.setProperty('--lg-filter', `url(#${filterId})`);
        }

        function refreshAllFilters() {
            document.querySelectorAll(document.body.dataset.liquidGlassTargets || '.lg, .floating-panel').forEach(el => {
                updateElementFilter(el);
            });
            pruneFilterCache();
        }

        /* ========== 自定义滑块 ========== */

        function setParameters({ refractionStrength, distortion } = {}) {
            if (typeof refractionStrength === 'number') currentRefractionStrength = refractionStrength;
            if (typeof distortion === 'number') currentDistortion = distortion;
        }

        // Compatibility aliases keep the page-level interaction code small while
        // the rendering implementation stays in this one shared module.
        window.applyLiquidGlassEffect = applyLiquidGlassEffect;
        window.updateElementFilter = updateElementFilter;
        window.refreshAllFilters = refreshAllFilters;
        window.LiquidGlass = { apply: applyLiquidGlassEffect, refresh: refreshAllFilters, setParameters };

})();
