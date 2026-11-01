/* PROTV home page tab.
 *
 * Served by PROTV as /PROTV/tab.js and loaded by the tab entry PROTV writes into the Custom
 * Tabs plugin configuration. It renders every channel the PROTV tuner serves (same numbering,
 * names and logos Jellyfin shows in Live TV) by reading /PROTV/Channels.
 *
 * Tapping a channel plays it: the tab asks /PROTV/Channels for the Jellyfin Live TV channel item
 * behind every PROTV channel and tells this app's own session to play it
 * (POST /Sessions/{sessionId}/Playing?playCommand=PlayNow). Playback then uses the normal Jellyfin
 * Live TV pipeline (tuner, guide, HLS/transcode), with no PROTV specific player involved.
 */
(function () {
    'use strict';

    if (window.ProTvTab) {
        window.ProTvTab.render();
        return;
    }

    var ROOT_ID = 'protvTabRoot';
    var GRID_ID = 'protvTabGrid';
    var MESSAGE_ID = 'protvTabMessage';
    var MORE_ID = 'protvTabMore';
    var STYLE_ID = 'protvTabStyles';
    var PLAYING_CLEAR_MS = 6000;
    // How often the tab checks whether the client opened its player after a play command, how
    // long it waits, and how many times it may ask (see watchPlayback).
    var PLAY_WATCH_MS = 500;
    var PLAY_WATCH_TICKS = 10;
    var PLAY_ATTEMPTS = 3;
    var state = {
        requested: false,
        loading: false,
        error: null,
        title: 'PROTV',
        total: 0,
        channels: [],
        filter: '',
        playing: null,
        message: null,
        messageKind: 'info',
        sessionId: null,
        // Which half of the tab is open: channels | movies | series.
        view: 'channels',
        vodLoading: false,
        vodError: null,
        vodFilter: '',
        vodPage: 1,
        vodPageCount: 0,
        vodTotal: 0,
        vodItems: [],
        vodCategories: [],
        vodCategoryId: null,
        vodSeries: null,
        // Dashboard groups, in display order, and the token for a parental PIN unlock.
        groups: [],
        unlockToken: null,
        // The "All" search view: one query across live channels, films and series.
        allQuery: '',
        allLoading: false,
        allError: null,
        allMovies: [],
        allSeries: [],
        // What is on now / next per PROTV channel id, filled in after the grid is drawn.
        now: {}
    };

    try {
        state.unlockToken = window.sessionStorage.getItem('protvParentalUnlock') || null;
    } catch (ignored) {
        state.unlockToken = null;
    }
    var waits = 0;
    var messageTimer = null;
    var vodSearchTimer = null;
    var VOD_PAGE_SIZE = 60;

    var css = [
        '.protvTabRoot{box-sizing:border-box;width:100%;padding:.5em 1.5em 3em;}',
        '.protvTabHeader{display:flex;flex-wrap:wrap;align-items:center;gap:.75em;margin:0 0 .5em;}',
        '.protvTabTitle{font-size:1.4em;font-weight:600;margin:0;}',
        '.protvTabCount{font-size:.85em;opacity:.7;margin-inline-end:auto;}',
        '.protvTabSearch,.protvTabButton{font:inherit;color:inherit;background:rgba(128,128,128,.15);',
        'border:1px solid rgba(128,128,128,.45);border-radius:4px;padding:.45em .8em;}',
        '.protvTabSearch{min-width:11em;}',
        '.protvTabButton{cursor:pointer;}',
        '.protvTabButton:hover{background:rgba(128,128,128,.32);}',
        '.protvTabButton[disabled]{opacity:.5;cursor:default;}',
        '.protvTabHint{font-size:.85em;opacity:.65;margin:0 0 1em;}',
        '.protvTabGrid{display:grid;gap:.75em;grid-template-columns:repeat(auto-fill,minmax(10.5em,1fr));}',
        '.protvTabCard{position:relative;display:flex;flex-direction:column;align-items:center;gap:.4em;',
        'padding:.85em .5em;border-radius:8px;background:rgba(128,128,128,.12);text-align:center;',
        'cursor:pointer;border:1px solid transparent;transition:background .15s,border-color .15s;}',
        '.protvTabCard:hover,.protvTabCard:focus{background:rgba(128,128,128,.24);border-color:rgba(128,128,128,.55);',
        'outline:none;}',
        '.protvTabCard[data-live="0"]{cursor:default;opacity:.6;}',
        '.protvTabCard[data-playing="1"]{border-color:#00a4dc;background:rgba(0,164,220,.22);}',
        '.protvTabLogo{display:flex;align-items:center;justify-content:center;width:100%;height:4.5em;}',
        '.protvTabLogo img{max-width:100%;max-height:100%;object-fit:contain;}',
        '.protvTabInitials{font-size:1.5em;font-weight:600;opacity:.75;letter-spacing:.05em;}',
        '.protvTabName{font-size:.95em;font-weight:500;word-break:break-word;}',
        '.protvTabNumber{font-size:.75em;opacity:.6;}',
        '.protvTabPlay{position:absolute;top:.35em;inset-inline-end:.35em;font-size:.9em;line-height:1;padding:.25em .4em;',
        'border:0;border-radius:4px;background:rgba(0,0,0,.35);color:#fff;cursor:pointer;opacity:0;}',
        '.protvTabCard:hover .protvTabPlay,.protvTabCard:focus .protvTabPlay{opacity:.85;}',
        '.protvTabPlay:hover{opacity:1;}',
        '.protvTabBadge{position:absolute;top:.35em;inset-inline-start:.35em;font-size:.9em;line-height:1;}',
        // A channel the last health check could not get data for: still openable (the answer may be
        // old), but visibly not worth waiting for.
        '.protvTabCardOffline{opacity:.45;}',
        '.protvTabCardOffline .protvTabLogo{filter:grayscale(1);}',
        '.protvTabOffline{position:absolute;bottom:.35em;inset-inline-end:.35em;font-size:.85em;opacity:.9;}',
        // The "On now" row: one chip per channel that is airing something, spanning the grid.
        '.protvTabNowRow{grid-column:1/-1;display:flex;gap:.6em;overflow-x:auto;padding:.1em 0 .6em;}',
        '.protvTabNowCard{flex:0 0 auto;min-width:12em;max-width:16em;background:rgba(128,128,128,.14);border-radius:.4em;padding:.5em .65em;cursor:pointer;text-align:start;}',
        '.protvTabNowCard:hover{background:rgba(128,128,128,.24);}',
        // Favourites / group sections on the channel tab.
        '.protvTabSectionTitle{grid-column:1/-1;font-size:1.05em;font-weight:600;margin:.5em 0 0;padding-top:.55em;',
        'border-top:1px solid rgba(128,128,128,.25);text-align:start;}',
        '.protvTabSectionTitle:first-child{border-top:0;padding-top:0;}',
        // A channel behind the parental PIN.
        '.protvTabCardLocked .protvTabLogo{filter:grayscale(1);opacity:.55;}',
        '.protvTabCardLocked .protvTabName{opacity:.75;}',
        '.protvTabLock{position:absolute;top:.35em;inset-inline-start:.35em;font-size:.9em;line-height:1;}',
        '.protvTabNowChannel{font-size:.8em;font-weight:600;opacity:.85;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;}',
        '.protvTabNowTitle{font-size:.9em;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;}',
        '.protvTabNowNext{font-size:.78em;opacity:.6;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;}',
        '.protvTabProgress{height:3px;border-radius:2px;background:rgba(128,128,128,.35);margin:.25em 0;}',
        '.protvTabProgressFill{height:100%;border-radius:2px;background:currentColor;opacity:.7;}',
        '.protvTabMessage{padding:.5em 0;opacity:.85;}',
        '.protvTabError{padding:1em;border-radius:6px;background:rgba(244,67,54,.18);}',
        '.protvTabNotice{padding:.6em 1em;border-radius:6px;background:rgba(0,164,220,.16);}',
        '.protvTabBad{padding:.6em 1em;border-radius:6px;background:rgba(255,152,0,.18);}',
        '.protvTabViews{display:flex;gap:.4em;}',
        '.protvTabView{font:inherit;color:inherit;background:rgba(128,128,128,.15);border:1px solid rgba(128,128,128,.45);',
        'border-radius:999px;padding:.35em .9em;cursor:pointer;opacity:.85;}',
        '.protvTabView:hover{background:rgba(128,128,128,.3);}',
        '.protvTabView[data-active="1"]{background:#00a4dc;border-color:#00a4dc;color:#fff;opacity:1;}',
        '.protvTabPosterGrid{display:grid;gap:1em;grid-template-columns:repeat(auto-fill,minmax(9em,1fr));}',
        '.protvTabPoster{display:flex;flex-direction:column;gap:.35em;cursor:pointer;outline:none;}',
        '.protvTabPosterArt{position:relative;display:flex;align-items:center;justify-content:center;',
        'border-radius:8px;overflow:hidden;background:rgba(128,128,128,.18);aspect-ratio:2/3;}',
        '.protvTabPosterArt img{width:100%;height:100%;object-fit:cover;display:block;}',
        '.protvTabPosterLabel{font-size:.9em;line-height:1.25;word-break:break-word;}',
        '.protvTabPosterMeta{font-size:.75em;opacity:.65;}',
        '.protvTabPosterPlay{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;',
        'background:rgba(0,0,0,.4);color:#fff;font-size:2em;opacity:0;transition:opacity .15s;}',
        '.protvTabPoster:hover .protvTabPosterPlay,.protvTabPoster:focus .protvTabPosterPlay{opacity:1;}',
        '.protvTabPoster[data-missing="1"] .protvTabPosterArt{opacity:.55;}',
        '.protvTabPoster[data-playing="1"] .protvTabPosterArt{box-shadow:0 0 0 2px #00a4dc;}',
        '.protvTabSeasons{display:block;}',
        '.protvTabSeasonTitle{font-weight:600;margin:1em 0 .4em;}',
        '.protvTabEpisodes{display:flex;flex-direction:column;gap:.35em;}',
        '.protvTabEpisode{display:flex;gap:.6em;align-items:center;padding:.45em .6em;border-radius:6px;',
        'background:rgba(128,128,128,.12);cursor:pointer;outline:none;}',
        '.protvTabEpisode:hover,.protvTabEpisode:focus{background:rgba(128,128,128,.26);}',
        '.protvTabEpisode[data-playing="1"]{background:rgba(0,164,220,.22);}',
        '.protvTabEpisodeNumber{font-weight:600;opacity:.7;min-width:2.4em;}',
        '.protvTabMore{margin-top:1em;}'
    ].join('');

    // ---- Interface language ---------------------------------------------------------------
    // The tab is injected into Jellyfin's own home page, so its markup cannot be rebuilt for a
    // second language. The English text the tab writes is swapped in the DOM instead, and one
    // observer keeps that in step with every later re-render. Arabic also flips the tab to right
    // to left; the styles above use logical properties (inset-inline, margin-inline) so the layout
    // mirrors with it. "auto" follows the browser, the dashboard setting wins otherwise.
    var language = resolveLanguage('auto');
    var languageObserver = null;
    var WATCHED = ['placeholder', 'title', 'aria-label'];
    // Text inside these is a value (a channel or a programme name), not interface text, so it is
    // never swapped even if it happens to look like one of the strings above.
    var VALUES = ['protvTabName', 'protvTabNowChannel', 'protvTabNowTitle', 'protvTabGroupName', 'protvTabPosterLabel'];

    var AR = {
        'Channels': 'القنوات',
        'Films': 'أفلام',
        'Series': 'مسلسلات',
        'All': 'الكل',
        'Favourites': 'المفضلة',
        'Other channels': 'قنوات أخرى',
        'Films and series': 'الأفلام والمسلسلات',
        'Search channels, films and series': 'ابحث في القنوات والأفلام والمسلسلات',
        'Searching...': 'جارٍ البحث...',
        'Search channels, films and series.': 'ابحث في القنوات والأفلام والمسلسلات.',
        'Type to search every live channel, film and series in PROTV.':
            'اكتب للبحث في كل قنوات البث المباشر والأفلام والمسلسلات في PROTV.',
        'PROTV is unavailable; showing the last channel list.':
            'PROTV غير متاح؛ تُعرض آخر قائمة قنوات محفوظة.',
        'Unlocked for this session.': 'تم الفتح لهذه الجلسة.',
        'That PIN was not accepted. Try again.': 'لم يُقبل الرمز السري. حاول مرة أخرى.',
        'Refresh': 'تحديث',
        'Refreshing...': 'جارٍ التحديث...',
        'Loading...': 'جارٍ التحميل...',
        'Loading channels...': 'جارٍ تحميل القنوات...',
        'Loading the PROTV catalogue...': 'جارٍ تحميل مكتبة PROTV...',
        'Filter channels': 'ابحث في القنوات',
        'Search films': 'ابحث في الأفلام',
        'Search series': 'ابحث في المسلسلات',
        'This series has no seasons listed yet.': 'لا توجد مواسم لهذا المسلسل بعد.',
        'This channel has no Jellyfin Live TV entry yet.': 'هذه القناة غير مضافة إلى البث المباشر في Jellyfin بعد.',
        'Sign in to Jellyfin again to see the PROTV channels.': 'أعد تسجيل الدخول إلى Jellyfin لعرض قنوات PROTV.',
        'Sign in to Jellyfin again to browse the PROTV catalogue.': 'أعد تسجيل الدخول إلى Jellyfin لتصفح مكتبة PROTV.',
        'Sign in to Jellyfin again to play this.': 'أعد تسجيل الدخول إلى Jellyfin للتشغيل.',
        'The PROTV channel list could not be loaded. Is the PROTV plugin still running?':
            'تعذّر تحميل قائمة قنوات PROTV. تأكد من أن إضافة PROTV ما زالت تعمل.',
        'The PROTV catalogue could not be loaded. Check the PROTV settings on the dashboard page.':
            'تعذّر تحميل مكتبة PROTV. تحقّق من إعدادات PROTV في لوحة التحكم.',
        'No channels yet. Add the PROTV tuner to Live TV (or press Refresh) and the channels PROTV returns will be listed here.':
            'لا توجد قنوات بعد. أضف موالف PROTV إلى البث المباشر (أو اضغط تحديث) وستظهر القنوات هنا.',
        'Pick a channel to play it here (the same Live TV channel the rest of Jellyfin plays). Use \u2139 on a channel to open its Jellyfin page instead.':
            'اختر قناة لتشغيلها هنا (نفس قناة البث المباشر التي يشغّلها Jellyfin). استخدم \u2139 لفتح صفحة القناة في Jellyfin.',
        'Films and episodes play here once they are in the PROTV VOD library (see the PROTV dashboard page). Series open their season list.':
            'تُشغَّل الأفلام والحلقات هنا بعد مزامنتها في مكتبة PROTV (راجع لوحة تحكم PROTV). المسلسلات تفتح قائمة المواسم.',
        'If Live TV has no channels yet, run the Jellyfin "Refresh Guide" task.':
            'إذا لم تظهر القنوات في البث المباشر، شغّل مهمة "تحديث دليل البرامج" في Jellyfin.',
        'If the PROTV VOD library is empty, run the VOD sync from the PROTV dashboard page.':
            'إذا كانت مكتبة PROTV فارغة، شغّل مزامنة VOD من لوحة تحكم PROTV.'
    };

    // Messages the tab assembles at run time: counts, titles and HTTP codes.
    var AR_PATTERNS = [
        [/^1 channel$/, 'قناة واحدة'],
        [/^(\d+) channels$/, '$1 قناة'],
        [/^(\d+) of (\d+) channels$/, '$1 من $2 قناة'],
        [/^(\d+) films$/, '$1 فيلم'],
        [/^(\d+) series$/, '$1 مسلسل'],
        [/^(\d+) of (\d+) films$/, '$1 من $2 فيلم'],
        [/^(\d+) of (\d+) series$/, '$1 من $2 مسلسل'],
        [/^Load more \((\d+) of (\d+)\)$/, 'تحميل المزيد ($1 من $2)'],
        [/^No channel matches "(.*)"\.$/, 'لا توجد قناة تطابق "$1".'],
        [/^No PROTV title matches "(.*)"\.$/, 'لا يوجد عنوان في PROTV يطابق "$1".'],
        [/^No results for "(.*)"\.$/, 'لا توجد نتائج لـ "$1".'],
        [/^(\d+) results$/, '$1 نتيجة'],
        [/^Enter the parental PIN to watch (.+)\.$/, 'أدخل رمز الرقابة الأبوية لمشاهدة $1.'],
        [/^Season (\d+)$/, 'الموسم $1'],
        [/^1 season$/, 'موسم واحد'],
        [/^(\d+) seasons$/, '$1 مواسم'],
        [/^1 min$/, 'دقيقة واحدة'],
        [/^(\d+) min$/, '$1 دقيقة'],
        [/^Open (.+) in Live TV$/, 'افتح $1 في البث المباشر'],
        [/^(.+) is not in Live TV yet$/, '$1 غير مضافة إلى البث المباشر بعد'],
        [/^(.+) has no stream right now$/, '$1 لا تبثّ حالياً'],
        [/^Open (.+)$/, 'افتح $1'],
        [/^Play (.+)$/, 'شغّل $1'],
        [/^Channel (.+)$/, 'قناة $1'],
        [/^Next: (.+)$/, 'التالي: $1'],
        [/^The PROTV channel list could not be loaded \(HTTP (\d+)\)\.$/, 'تعذّر تحميل قائمة قنوات PROTV (HTTP $1).'],
        [/^The PROTV catalogue could not be loaded \(HTTP (\d+)\)\.$/, 'تعذّر تحميل مكتبة PROTV (HTTP $1).'],
        [/^1 channel is not in Jellyfin Live TV yet\. Run the "Refresh Guide" task in the Jellyfin dashboard, then press Refresh\.$/,
            'قناة واحدة غير مضافة إلى البث المباشر في Jellyfin بعد. شغّل مهمة "تحديث دليل البرامج" ثم اضغط تحديث.'],
        [/^(\d+) channels are not in Jellyfin Live TV yet\. Run the "Refresh Guide" task in the Jellyfin dashboard, then press Refresh\.$/,
            '$1 قناة غير مضافة إلى البث المباشر في Jellyfin بعد. شغّل مهمة "تحديث دليل البرامج" ثم اضغط تحديث.'],
        [/^"(.+)" is not in the PROTV VOD library yet\. Open the PROTV dashboard page, press "Sync now" and come back once it has run\.$/,
            '"$1" غير متاح في مكتبة PROTV بعد. افتح لوحة تحكم PROTV، اضغط "مزامنة الآن" ثم أعد المحاولة.'],
        [/^"(.+)" could not be started \(HTTP (\d+)\)\.(.*)$/, function (match) {
            var tail = match[3].trim();
            return '"' + match[1] + '" تعذّر تشغيله (HTTP ' + match[2] + ').' + (tail ? ' ' + translate(tail) : '');
        }]
    ];

    function isArabic() {
        return language === 'ar';
    }

    // Resolves "auto" against the browser: an Arabic browser gets Arabic.
    function resolveLanguage(value) {
        var wanted = String(value || 'auto').toLowerCase();
        if (wanted === 'ar' || wanted === 'en') {
            return wanted;
        }

        var browser = String((navigator.languages && navigator.languages[0]) || navigator.language || 'en').toLowerCase();
        return browser.indexOf('ar') === 0 ? 'ar' : 'en';
    }

    function translate(text) {
        var value = String(text == null ? '' : text);
        var key = value.trim();
        if (!isArabic() || !key) {
            return value;
        }

        if (Object.prototype.hasOwnProperty.call(AR, key)) {
            return value.replace(key, AR[key]);
        }

        for (var i = 0; i < AR_PATTERNS.length; i++) {
            var match = AR_PATTERNS[i][0].exec(key);
            if (!match) {
                continue;
            }

            var replacement = AR_PATTERNS[i][1];
            if (typeof replacement === 'function') {
                return replacement(match);
            }

            var swapped = replacement.replace(/\$(\d)/g, function (whole, index) {
                return match[Number(index)];
            });
            return value.replace(key, swapped);
        }

        return value;
    }

    function localize(root) {
        if (!root) {
            return;
        }

        var arabic = isArabic();
        root.setAttribute('dir', arabic ? 'rtl' : 'ltr');
        root.setAttribute('lang', arabic ? 'ar' : 'en');
        if (arabic) {
            translateElement(root);
        }
    }

    function isValue(node) {
        if (!node.classList) {
            return false;
        }

        for (var i = 0; i < VALUES.length; i++) {
            if (node.classList.contains(VALUES[i])) {
                return true;
            }
        }

        return false;
    }

    function translateElement(node) {
        var children = node.childNodes;
        for (var i = 0; i < children.length; i++) {
            var child = children[i];
            if (child.nodeType === 3) {
                var text = String(child.nodeValue || '');
                var trimmed = text.trim();
                if (trimmed) {
                    var swapped = translate(trimmed);
                    if (swapped !== trimmed) {
                        child.nodeValue = text.replace(trimmed, swapped);
                    }
                }

                continue;
            }

            if (child.nodeType !== 1) {
                continue;
            }

            var tag = child.tagName;
            if (tag === 'SCRIPT' || tag === 'STYLE') {
                continue;
            }

            if (isValue(child)) {
                continue;
            }

            for (var a = 0; a < WATCHED.length; a++) {
                var attribute = WATCHED[a];
                var value = child.getAttribute(attribute);
                if (value) {
                    child.setAttribute(attribute, translate(value));
                }
            }

            translateElement(child);
        }
    }

    // Keeps the translation in step with the tab's own re-renders. A pass writes into the same
    // nodes and attributes, which would retrigger the observer, so it is detached while translating.
    // Attributes are watched by name because the tab sets placeholders and titles straight after
    // a re-render; without that they would flip back to English.
    var WATCH_OPTIONS = {
        childList: true,
        subtree: true,
        characterData: true,
        attributes: true,
        attributeFilter: WATCHED
    };

    function watchLanguage(host) {
        if (!host) {
            return;
        }

        localize(host);
        if (languageObserver || !window.MutationObserver) {
            return;
        }

        languageObserver = new window.MutationObserver(function () {
            languageObserver.disconnect();
            localize(host);
            if (languageObserver) {
                languageObserver.observe(host, WATCH_OPTIONS);
            }
        });
        languageObserver.observe(host, WATCH_OPTIONS);
    }

    function ensureStyles() {
        if (document.getElementById(STYLE_ID)) {
            return;
        }

        var style = document.createElement('style');
        style.id = STYLE_ID;
        style.type = 'text/css';
        style.appendChild(document.createTextNode(css));
        document.head.appendChild(style);
    }

    function url(path, query) {
        var full = window.ApiClient && ApiClient.getUrl ? ApiClient.getUrl(path) : '/' + path;
        return query ? full + query : full;
    }

    // The parental unlock is a short-lived HMAC token bound to this Jellyfin user, not a Jellyfin
    // token. Send it as a header rather than a query parameter so it cannot end up in reverse-proxy
    // access logs, browser history or Referer headers. ApiClient.ajax supports headers; plain
    // getJSON does not, so use getJSON only when there is no unlock token.
    function getJson(full) {
        if (!state.unlockToken || !ApiClient.ajax) {
            return ApiClient.getJSON(full);
        }

        return ApiClient.ajax({
            type: 'GET',
            url: full,
            dataType: 'json',
            headers: { 'X-PROTV-Unlock': state.unlockToken }
        });
    }

    function initials(name) {
        var parts = String(name || '').trim().split(/\s+/).filter(Boolean);
        var text = parts.length > 1
            ? parts[0].charAt(0) + parts[1].charAt(0)
            : String(name || '?').substring(0, 2);
        return text.toUpperCase();
    }

    // Loads the channel list. refresh=true asks the PROTV service again; otherwise the channel
    // list saved by the tuner / guide refresh is used, so the tab opens instantly.
    function load(refresh) {
        state.requested = true;
        state.loading = true;
        state.error = null;
        renderGrid();

        getJson(url('PROTV/Channels', refresh ? '?refresh=true' : '')).then(function (data) {
            state.loading = false;
            state.title = (data && data.title) || 'PROTV';
            state.channels = (data && data.channels) || [];
            state.total = (data && data.total) || state.channels.length;
            state.groups = (data && data.groups) || [];

            // The dashboard setting decides the language; "auto" already resolved to the browser.
            var next = resolveLanguage(data && data.language);
            if (next !== language) {
                language = next;
                localize(document.getElementById(ROOT_ID));
            }

            renderHeader();
            renderGrid();
            loadNow();
        }, function (error) {
            state.loading = false;
            state.error = describe(error);
            renderHeader();
            renderGrid();
        });
    }

    // ---- what is on now ----------------------------------------------------------
    // Asked for after the channel list, so the grid and the channel search appear at once. The
    // answer fills the "On now" row and the now / next line on every card. It is a nice-to-have:
    // without it the tab still lists and plays every channel.
    function loadNow() {
        state.now = {};
        return getJson(url('PROTV/Channels/Now')).then(function (data) {
            var map = {};
            ((data && data.channels) || []).forEach(function (entry) {
                map[entry.channelId] = entry;
            });
            state.now = map;
            renderGrid();
        }, function () {
            // Leave the row and the now / next lines out.
        });
    }

    function nowFor(channel) {
        return channel ? state.now[channel.id] : null;
    }

    // One chip per channel that is airing something right now, the most recently started first, so
    // the row reads like a guide strip. Capped so a large lineup does not build hundreds of nodes.
    function nowRow() {
        var entries = visibleChannels().map(function (channel) {
            var entry = nowFor(channel);
            return entry && entry.now ? { channel: channel, entry: entry } : null;
        }).filter(function (item) {
            return item !== null;
        });
        if (!entries.length) {
            return null;
        }

        entries.sort(function (left, right) {
            return startOf(right.entry.now) - startOf(left.entry.now);
        });

        var row = document.createElement('div');
        row.className = 'protvTabNowRow';
        entries.slice(0, 24).forEach(function (item) {
            row.appendChild(nowCard(item.channel, item.entry));
        });
        return row;
    }

    function nowCard(channel, entry) {
        var chip = document.createElement('div');
        chip.className = 'protvTabNowCard';
        chip.setAttribute('role', 'button');
        chip.setAttribute('tabindex', '0');
        chip.setAttribute('title', 'Play ' + channel.name);

        var name = document.createElement('div');
        name.className = 'protvTabNowChannel';
        name.textContent = channel.name || '';

        var title = document.createElement('div');
        title.className = 'protvTabNowTitle';
        title.textContent = entry.now.title || '';

        var bar = document.createElement('div');
        bar.className = 'protvTabProgress';
        var fill = document.createElement('div');
        fill.className = 'protvTabProgressFill';
        fill.style.width = progressWidth(entry.now) + '%';
        bar.appendChild(fill);

        var next = document.createElement('div');
        next.className = 'protvTabNowNext';
        next.textContent = entry.next && entry.next.title ? 'Next: ' + entry.next.title : '';

        chip.appendChild(name);
        chip.appendChild(title);
        chip.appendChild(bar);
        chip.appendChild(next);
        chip.addEventListener('click', function () {
            playChannel(channel);
        });
        chip.addEventListener('keydown', function (event) {
            if (event.key === 'Enter' || event.key === ' ' || event.key === 'Spacebar') {
                event.preventDefault();
                playChannel(channel);
            }
        });

        return chip;
    }

    function startOf(programme) {
        return programme && programme.startsUtc ? new Date(programme.startsUtc).getTime() : 0;
    }

    function progressWidth(programme) {
        var start = startOf(programme);
        var end = programme && programme.endsUtc ? new Date(programme.endsUtc).getTime() : 0;
        if (!start || end <= start) {
            return 0;
        }

        var ratio = (Date.now() - start) / (end - start);
        return Math.round(Math.max(0, Math.min(1, ratio)) * 100);
    }

    function describe(error) {
        var status = error && (error.status || error.statusCode);
        if (status === 401 || status === 403) {
            return 'Sign in to Jellyfin again to see the PROTV channels.';
        }

        if (status) {
            return 'The PROTV channel list could not be loaded (HTTP ' + status + ').';
        }

        return 'The PROTV channel list could not be loaded. Is the PROTV plugin still running?';
    }

    function visibleChannels() {
        var filter = state.filter.trim().toLowerCase();
        if (!filter) {
            return state.channels;
        }

        return state.channels.filter(function (channel) {
            return String(channel.name || '').toLowerCase().indexOf(filter) >= 0
                || String(channel.number || '') === filter
                || String(groupNameFor(channel) || '').toLowerCase().indexOf(filter) >= 0;
        });
    }

    function groupNameFor(channel) {
        var id = String((channel && channel.groupId) || '');
        if (!id) {
            return '';
        }

        for (var index = 0; index < state.groups.length; index++) {
            if (String(state.groups[index].id) === id) {
                return state.groups[index].name || '';
            }
        }

        return '';
    }

    // Favourites first, then the dashboard groups in order, then the channels that are in neither.
    // A channel appears once: a favourite that is also in a group stays in Favourites only.
    function channelSections(channels) {
        var hasFavourites = false;
        var index;
        for (index = 0; index < channels.length; index++) {
            if (channels[index].favorite === true) {
                hasFavourites = true;
                break;
            }
        }

        if (!hasFavourites && !state.groups.length) {
            return [{ name: '', group: false, channels: channels }];
        }

        var sections = [];
        var shown = {};
        var favourites = channels.filter(function (channel) {
            return channel.favorite === true;
        });
        if (favourites.length) {
            sections.push({ name: 'Favourites', group: false, channels: favourites });
            favourites.forEach(function (channel) { shown[channel.id] = true; });
        }

        state.groups.forEach(function (group) {
            var items = channels.filter(function (channel) {
                return !shown[channel.id] && String(channel.groupId || '') === String(group.id);
            });
            if (!items.length) {
                return;
            }

            sections.push({ name: group.name || '', group: true, channels: items });
            items.forEach(function (channel) { shown[channel.id] = true; });
        });

        var other = channels.filter(function (channel) { return !shown[channel.id]; });
        if (other.length) {
            sections.push({ name: 'Other channels', group: false, channels: other });
        }

        return sections;
    }

    // ---- VOD: films and series --------------------------------------------
    // The catalogue is paged straight out of PROTV (nothing large is kept in the browser), and
    // every title the plugin has already synced carries the id of the Jellyfin item it became, so
    // tapping a film plays it like any other Jellyfin item.
    function pad2(value) {
        var number = parseInt(value, 10) || 0;
        return (number < 10 ? '0' : '') + number;
    }

    function vodSection() {
        return state.view === 'series' ? 'Series' : 'Movies';
    }

    function loadVod(page, append) {
        state.vodLoading = true;
        state.vodError = null;
        state.vodPage = page || 1;
        if (!append) {
            state.vodItems = [];
            state.vodSeries = null;
            renderHeader();
            renderGrid();
        }

        var query = '?page=' + state.vodPage + '&pageSize=' + VOD_PAGE_SIZE;
        if (state.vodFilter) {
            query += '&search=' + encodeURIComponent(state.vodFilter);
        }

        if (state.vodCategoryId) {
            query += '&categoryId=' + encodeURIComponent(state.vodCategoryId);
        }

        getJson(url('PROTV/Vod/' + vodSection(), query)).then(function (data) {
            var items = (data && data.items) || [];
            state.vodLoading = false;
            state.vodItems = append ? state.vodItems.concat(items) : items;
            state.vodTotal = (data && data.total) || 0;
            state.vodPageCount = (data && data.pageCount) || 0;
            state.vodCategories = (data && data.categories) || [];
            renderHeader();
            renderGrid();
        }, function (error) {
            state.vodLoading = false;
            state.vodError = describeVod(error);
            renderHeader();
            renderGrid();
        });
    }

    function loadSeriesDetail(series) {
        state.vodLoading = true;
        state.vodError = null;
        renderGrid();
        getJson(url('PROTV/Vod/SeriesDetail', '?seriesId=' + encodeURIComponent(series.id)))
            .then(function (data) {
                state.vodLoading = false;
                state.vodSeries = data;
                renderGrid();
                renderHeader();
            }, function (error) {
                state.vodLoading = false;
                state.vodError = describeVod(error);
                renderGrid();
            });
    }

    function describeVod(error) {
        var status = error && (error.status || error.statusCode);
        if (status === 401 || status === 403) {
            return 'Sign in to Jellyfin again to browse the PROTV catalogue.';
        }

        if (status) {
            return 'The PROTV catalogue could not be loaded (HTTP ' + status + ').';
        }

        return 'The PROTV catalogue could not be loaded. Check the PROTV settings on the dashboard page.';
    }

    function switchView(view) {
        if (state.view === view) {
            return;
        }

        state.view = view;
        state.vodFilter = '';
        state.vodCategoryId = null;
        state.vodSeries = null;
        state.vodItems = [];
        state.vodError = null;
        state.vodPage = 1;
        var search = document.querySelector('#' + ROOT_ID + ' .protvTabSearch');
        if (search) {
            search.value = view === 'channels' ? state.filter : '';
        }

        if (view === 'all') {
            state.allQuery = '';
            state.allLoading = false;
            state.allError = null;
            state.allMovies = [];
            state.allSeries = [];
            renderHeader();
            renderGrid();
            return;
        }

        renderHeader();
        renderGrid();
        if (view !== 'channels' && !state.vodItems.length) {
            loadVod(1);
        }
    }

    function renderVodBody() {
        var grid = document.getElementById(GRID_ID);
        var message = document.getElementById(MESSAGE_ID);
        var more = document.getElementById(MORE_ID);
        if (!grid || !message) {
            return;
        }

        grid.textContent = '';
        if (more) {
            more.style.display = 'none';
        }

        if (state.vodSeries) {
            renderSeriesDetail(grid, message);
            return;
        }

        if (state.vodError) {
            setMessage(state.vodError, 'bad');
            return;
        }

        if (state.vodLoading && !state.vodItems.length) {
            message.className = 'protvTabMessage';
            message.textContent = 'Loading the PROTV catalogue...';
            return;
        }

        if (!state.vodItems.length) {
            message.className = 'protvTabMessage';
            message.textContent = state.vodFilter
                ? 'No PROTV title matches "' + state.vodFilter + '".'
                : 'The PROTV catalogue returned no titles.';
            return;
        }

        renderMessage();
        grid.className = 'protvTabPosterGrid';
        var fragment = document.createDocumentFragment();
        for (var index = 0; index < state.vodItems.length; index++) {
            fragment.appendChild(vodCard(state.vodItems[index]));
        }

        grid.appendChild(fragment);

        if (more && state.vodPage < state.vodPageCount) {
            more.style.display = '';
            more.textContent = 'Load more (' + state.vodItems.length + ' of ' + state.vodTotal + ')';
        }
    }

    function vodCard(item) {
        var isSeries = item.kind === 'series';
        var missing = !isSeries && (!item.itemId || item.inLibrary === false);
        var node = document.createElement('div');
        node.className = 'protvTabPoster';
        node.setAttribute('role', 'button');
        node.setAttribute('tabindex', '0');
        node.setAttribute('data-playing', state.playing === 'vod:' + item.id ? '1' : '0');
        node.setAttribute('data-missing', missing ? '1' : '0');
        node.setAttribute('title', isSeries ? 'Open ' + item.title : 'Play ' + item.title);

        var art = document.createElement('div');
        art.className = 'protvTabPosterArt';
        if (item.posterUrl) {
            var image = document.createElement('img');
            image.loading = 'lazy';
            image.alt = item.title || '';
            image.src = item.posterUrl;
            image.addEventListener('error', function () {
                if (image.parentNode) {
                    art.removeChild(image);
                    art.appendChild(initialsNode(item.title));
                }
            });
            art.appendChild(image);
        } else {
            art.appendChild(initialsNode(item.title));
        }

        var overlay = document.createElement('div');
        overlay.className = 'protvTabPosterPlay';
        overlay.textContent = isSeries ? '\u2261' : '\u25b6';
        art.appendChild(overlay);

        var label = document.createElement('div');
        label.className = 'protvTabPosterLabel';
        label.textContent = item.title || '';

        var meta = document.createElement('div');
        meta.className = 'protvTabPosterMeta';
        meta.textContent = vodMeta(item, missing);

        node.appendChild(art);
        node.appendChild(label);
        node.appendChild(meta);

        var open = function () {
            if (isSeries) {
                if (state.view === 'all') {
                    state.view = 'series';
                    state.vodFilter = '';
                    state.vodItems = [];
                    state.vodSeries = null;
                    state.vodError = null;
                    renderHeader();
                }

                loadSeriesDetail(item);
            } else {
                playVod(item);
            }
        };
        node.addEventListener('click', open);
        node.addEventListener('keydown', function (event) {
            if (event.key === 'Enter' || event.key === ' ' || event.key === 'Spacebar') {
                event.preventDefault();
                open();
            }
        });

        return node;
    }

    function vodMeta(item, missing) {
        var parts = [];
        if (item.kind === 'series') {
            if (item.seasonsCount) {
                parts.push(item.seasonsCount + (item.seasonsCount === 1 ? ' season' : ' seasons'));
            }
        } else if (item.kind === 'episode') {
            parts.push('S' + pad2(item.seasonNumber || 1) + 'E' + pad2(item.episodeNumber || 1));
        }

        if (item.year) {
            parts.push(String(item.year));
        }

        if (item.durationMinutes) {
            parts.push(item.durationMinutes + ' min');
        }

        if (item.rating) {
            parts.push('\u2605 ' + item.rating.toFixed(1));
        }

        if (missing) {
            parts.push('not synced yet');
        }

        return parts.join(' \u00b7 ');
    }

    function renderSeriesDetail(grid, message) {
        var data = state.vodSeries;
        var series = (data && data.series) || {};
        var seasons = (data && data.seasons) || [];
        message.className = 'protvTabMessage';
        message.textContent = '';
        grid.className = 'protvTabSeasons';

        var back = document.createElement('button');
        back.className = 'protvTabButton';
        back.type = 'button';
        back.textContent = '\u2190 Back to ' + vodSection().toLowerCase();
        back.addEventListener('click', function () {
            state.vodSeries = null;
            renderGrid();
            renderHeader();
        });
        grid.appendChild(back);

        var heading = document.createElement('div');
        heading.className = 'protvTabSeasonTitle';
        heading.textContent = series.title || 'Series';
        grid.appendChild(heading);

        if (!seasons.length) {
            var empty = document.createElement('p');
            empty.className = 'protvTabMessage';
            empty.textContent = 'This series has no seasons listed yet.';
            grid.appendChild(empty);
            return;
        }

        seasons.forEach(function (season) {
            var title = document.createElement('div');
            title.className = 'protvTabSeasonTitle';
            title.textContent = season.title || ('Season ' + season.index);
            grid.appendChild(title);

            var list = document.createElement('div');
            list.className = 'protvTabEpisodes';
            (season.episodes || []).forEach(function (episode) {
                list.appendChild(episodeRow(episode));
            });
            grid.appendChild(list);
        });
    }

    function episodeRow(episode) {
        var row = document.createElement('div');
        row.className = 'protvTabEpisode';
        row.setAttribute('role', 'button');
        row.setAttribute('tabindex', '0');
        row.setAttribute('data-playing', state.playing === 'vod:' + episode.id ? '1' : '0');

        var number = document.createElement('span');
        number.className = 'protvTabEpisodeNumber';
        number.textContent = 'E' + pad2(episode.episodeNumber || 1);

        var title = document.createElement('span');
        title.textContent = episode.title || '';

        var meta = document.createElement('span');
        meta.className = 'protvTabPosterMeta';
        meta.textContent = (!episode.itemId || episode.inLibrary === false)
            ? 'not synced yet'
            : (episode.durationMinutes ? episode.durationMinutes + ' min' : '');

        row.appendChild(number);
        row.appendChild(title);
        row.appendChild(meta);

        var open = function () {
            playVod(episode);
        };
        row.addEventListener('click', open);
        row.addEventListener('keydown', function (event) {
            if (event.key === 'Enter' || event.key === ' ' || event.key === 'Spacebar') {
                event.preventDefault();
                open();
            }
        });

        return row;
    }

    // ---- playback ---------------------------------------------------------
    // The tab does not start playback itself: it tells this app's session to play the Jellyfin
    // Live TV channel item, exactly like the Jellyfin remote control / "play on" feature.
    function currentSessionId() {
        if (state.sessionId) {
            return Promise.resolve(state.sessionId);
        }

        var deviceId = ApiClient.deviceId ? ApiClient.deviceId() : null;
        var query = {};
        if (deviceId) {
            query.deviceId = deviceId;
        }

        return ApiClient.getSessions(query).then(function (sessions) {
            var list = sessions || [];
            var userId = ApiClient.getCurrentUserId ? ApiClient.getCurrentUserId() : null;
            var own = null;
            var index;
            for (index = 0; index < list.length; index++) {
                if (deviceId && list[index].DeviceId === deviceId) {
                    own = list[index];
                    break;
                }
            }

            if (!own && userId) {
                for (index = 0; index < list.length; index++) {
                    if (list[index].UserId === userId) {
                        own = list[index];
                        break;
                    }
                }
            }

            if (!own && list.length === 1) {
                own = list[0];
            }

            if (!own || !own.Id) {
                throw new Error('no-session');
            }

            state.sessionId = own.Id;
            return state.sessionId;
        });
    }

    function startPlayback(channel) {
        return currentSessionId().then(function (sessionId) {
            var playUrl = ApiClient.getUrl('Sessions/' + sessionId + '/Playing')
                + '?playCommand=PlayNow&itemIds=' + encodeURIComponent(channel.itemId);
            // No dataType: the endpoint answers 204 with an empty body.
            return ApiClient.ajax({ type: 'POST', url: playUrl });
        });
    }

    // True once the client has visibly reacted to a play command by opening its player.
    function playerStarted() {
        if (!document.getElementById(GRID_ID)) {
            return true; // the client replaced the tab with its player page
        }

        if (window.location.hash.indexOf('video') >= 0) {
            return true;
        }

        var videos = document.querySelectorAll('video');
        for (var index = 0; index < videos.length; index++) {
            if (String(videos[index].currentSrc || videos[index].src || '')) {
                return true;
            }
        }

        return false;
    }

    function unlockChannel(channel) {
        var pin = window.prompt(translate('Enter the parental PIN to watch ' + channel.name + '.'));
        if (pin === null || !pin) {
            return;
        }

        ApiClient.ajax({
            type: 'POST',
            url: url('PROTV/Parental/Unlock'),
            data: JSON.stringify({ Pin: pin }),
            contentType: 'application/json',
            dataType: 'json'
        }).then(function (result) {
            if (!result || !result.token) {
                throw new Error('no-token');
            }

            state.unlockToken = result.token;
            try {
                window.sessionStorage.setItem('protvParentalUnlock', result.token);
            } catch (ignored) {
                // Private browsing may refuse storage; the unlock still works for this page.
            }

            state.channels.forEach(function (item) {
                item.locked = false;
            });

            renderGrid();
            setMessage('Unlocked for this session.', 'notice');
            playChannel(channel);
        }, function () {
            setMessage('That PIN was not accepted. Try again.', 'bad');
        });
    }

    function playChannel(channel) {
        if (channel.locked === true) {
            unlockChannel(channel);
            return;
        }

        if (state.playing === channel.number) {
            return;
        }

        if (!channel.itemId) {
            setMessage('This channel has no Jellyfin Live TV entry yet.', 'bad');
            return;
        }

        if (channel.inLiveTv === false) {
            setMessage('"' + channel.name + '" is not in Live TV yet. Press Refresh (or run the '
                + 'Jellyfin "Refresh Guide" task) and try again.', 'bad');
            return;
        }

        state.playing = channel.number;
        renderGrid();
        setMessage('Starting ' + channel.name + '...', 'notice');
        sendPlay(playable(channel.number, channel.name, channel.itemId, true), 1);
    }

    // Plays a film or an episode from the VOD catalogue. They are ordinary Jellyfin items once the
    // plugin has synced them, so they start exactly the way a live channel does.
    function playVod(item) {
        var key = 'vod:' + item.id;
        if (state.playing === key) {
            return;
        }

        if (!item.itemId || item.inLibrary === false) {
            setMessage('"' + item.title + '" is not in the PROTV VOD library yet. Open the PROTV '
                + 'dashboard page, press "Sync now" and come back once it has run.', 'bad');
            return;
        }

        state.playing = key;
        renderGrid();
        renderHeader();
        setMessage('Starting ' + item.title + '...', 'notice');
        sendPlay(playable(key, item.title, item.itemId, false), 1);
    }

    // What a play command needs: a key to highlight the card, a label for the messages and the
    // Jellyfin item to play. Live TV channels and VOD titles go through the same pipeline.
    function playable(key, label, itemId, isChannel) {
        return { key: key, label: label, itemId: itemId, isChannel: isChannel };
    }

    function sendPlay(target, attempt) {
        startPlayback(target).then(function () {
            watchPlayback(target, attempt);
        }, function (error) {
            var status = error && (error.status || error.statusCode);
            if (attempt < PLAY_ATTEMPTS && status !== 401 && status !== 403) {
                window.setTimeout(function () {
                    sendPlay(target, attempt + 1);
                }, PLAY_WATCH_MS);
                return;
            }

            finishPlay(playError(target, error));
        });
    }

    // The client answers by opening its player. That can take a few seconds, and the very first
    // play in a freshly loaded page can be swallowed while the client is still loading its player,
    // so ask again when nothing happens.
    function watchPlayback(target, attempt) {
        var ticks = 0;
        var timer = window.setInterval(function () {
            ticks++;
            if (playerStarted()) {
                window.clearInterval(timer);
                window.setTimeout(function () {
                    if (state.playing === target.key) {
                        state.playing = null;
                        renderGrid();
                        renderHeader();
                    }
                }, PLAYING_CLEAR_MS);
                return;
            }

            if (ticks < PLAY_WATCH_TICKS) {
                return;
            }

            window.clearInterval(timer);
            if (attempt < PLAY_ATTEMPTS) {
                sendPlay(target, attempt + 1);
                return;
            }

            finishPlay('"' + target.label + '" did not start. Check that the stream is online, '
                + 'then try again.');
        }, PLAY_WATCH_MS);
    }

    function finishPlay(message) {
        state.playing = null;
        renderGrid();
        renderHeader();
        if (message) {
            setMessage(message, 'bad');
        }
    }

    function playError(target, error) {
        if (error && error.message === 'no-session') {
            return 'Jellyfin could not find this app session. Reload the page and try again.';
        }

        var status = error && (error.status || error.statusCode);
        if (status === 401 || status === 403) {
            return 'Sign in to Jellyfin again to play this.';
        }

        if (status) {
            var hint = target.isChannel
                ? ' If Live TV has no channels yet, run the Jellyfin "Refresh Guide" task.'
                : ' If the PROTV VOD library is empty, run the VOD sync from the PROTV dashboard page.';
            return '"' + target.label + '" could not be started (HTTP ' + status + ').' + hint;
        }

        return '"' + target.label + '" could not be started. The PROTV service or the stream may be '
            + 'offline.';
    }

    // Opens the channel the way the rest of Jellyfin shows it (details page with its Play button).
    function openChannel(channel) {
        if (!channel.itemId) {
            setMessage('This channel has no Jellyfin Live TV entry yet.', 'bad');
            return;
        }

        window.location.hash = '#/details?id=' + encodeURIComponent(channel.itemId);
    }

    // ---- rendering --------------------------------------------------------
    function setMessage(text, kind) {
        state.message = text;
        state.messageKind = kind || 'info';
        renderMessage();
        if (messageTimer) {
            window.clearTimeout(messageTimer);
        }

        if (text && (kind === 'notice' || kind === 'bad')) {
            messageTimer = window.setTimeout(function () {
                if (state.message === text) {
                    state.message = null;
                    renderMessage();
                }
            }, 8000);
        }
    }

    function renderMessage() {
        var message = document.getElementById(MESSAGE_ID);
        if (!message) {
            return;
        }

        message.className = 'protvTabMessage';
        message.textContent = '';
        if (!state.message) {
            return;
        }

        message.className = 'protvTabMessage ' + (state.messageKind === 'bad' ? 'protvTabBad' : 'protvTabNotice');
        message.textContent = state.message;
    }

    function card(channel) {
        var item = document.createElement('div');
        // The last health check may have found no stream behind this channel; the card says so
        // instead of letting the tap end in a spinner. It stays clickable because the answer can be
        // out of date, and a channel that came back is fixed by the next check.
        var offline = channel.health === 'offline';
        var locked = channel.locked === true;
        item.className = 'protvTabCard'
            + (offline ? ' protvTabCardOffline' : '')
            + (locked ? ' protvTabCardLocked' : '');
        item.setAttribute('role', 'button');
        item.setAttribute('tabindex', '0');
        item.setAttribute('data-number', channel.number || '');
        item.setAttribute('data-live', channel.inLiveTv === false ? '0' : '1');
        item.setAttribute('data-health', channel.health || 'unknown');
        item.setAttribute('data-locked', locked ? '1' : '0');
        item.setAttribute('data-playing', state.playing === channel.number ? '1' : '0');
        item.setAttribute('title', locked
            ? 'Enter the parental PIN to watch ' + channel.name
            : channel.inLiveTv === false
                ? channel.name + ' is not in Live TV yet'
                : offline
                    ? channel.name + ' has no stream right now'
                    : 'Play ' + channel.name);

        var logo = document.createElement('div');
        logo.className = 'protvTabLogo';
        if (channel.logoUrl) {
            var image = document.createElement('img');
            image.loading = 'lazy';
            image.alt = channel.name || '';
            image.src = channel.logoUrl;
            image.addEventListener('error', function () {
                if (image.parentNode) {
                    logo.removeChild(image);
                    logo.appendChild(initialsNode(channel.name));
                }
            });
            logo.appendChild(image);
        } else {
            logo.appendChild(initialsNode(channel.name));
        }

        var name = document.createElement('div');
        name.className = 'protvTabName';
        name.textContent = channel.name || '';

        var number = document.createElement('div');
        number.className = 'protvTabNumber';
        number.textContent = 'Channel ' + (channel.number || '');

        item.appendChild(logo);
        item.appendChild(name);
        item.appendChild(number);

        // What is on now (and what follows) from the "On now" answer, when it has arrived.
        var airing = nowFor(channel);
        if (airing && airing.now && airing.now.title) {
            var nowLine = document.createElement('div');
            nowLine.className = 'protvTabNowTitle';
            nowLine.textContent = airing.now.title;
            item.appendChild(nowLine);

            if (airing.next && airing.next.title) {
                var nextLine = document.createElement('div');
                nextLine.className = 'protvTabNowNext';
                nextLine.textContent = 'Next: ' + airing.next.title;
                item.appendChild(nextLine);
            }
        }

        if (state.playing === channel.number) {
            var badge = document.createElement('span');
            badge.className = 'protvTabBadge';
            badge.textContent = '\u25B6'; // play glyph while the channel is starting
            item.appendChild(badge);
        }

        if (locked) {
            var lock = document.createElement('span');
            lock.className = 'protvTabLock';
            lock.textContent = '\uD83D\uDD12'; // parental lock
            item.appendChild(lock);
        }

        if (offline) {
            var mark = document.createElement('span');
            mark.className = 'protvTabOffline';
            mark.textContent = '\u26A0'; // no stream the last time it was checked
            item.appendChild(mark);
        }

        // Second action: the channel page, for anyone who wants the guide entry or a different
        // player instead of playing straight away.
        if (channel.itemId) {
            var open = document.createElement('button');
            open.className = 'protvTabPlay';
            open.type = 'button';
            open.textContent = '\u2139'; // info glyph
            open.setAttribute('title', 'Open ' + channel.name + ' in Live TV');
            open.addEventListener('click', function (event) {
                event.stopPropagation();
                event.preventDefault();
                openChannel(channel);
            });
            item.appendChild(open);
        }

        item.addEventListener('click', function () {
            if (locked) {
                unlockChannel(channel);
            } else {
                playChannel(channel);
            }
        });
        item.addEventListener('keydown', function (event) {
            if (event.key === 'Enter' || event.key === ' ' || event.key === 'Spacebar') {
                event.preventDefault();
                if (locked) {
                    unlockChannel(channel);
                } else {
                    playChannel(channel);
                }
            }
        });

        return item;
    }

    function initialsNode(name) {
        var node = document.createElement('span');
        node.className = 'protvTabInitials';
        node.textContent = initials(name);
        return node;
    }

    // The tab shell: header + grid + message. Built once; only the pieces change afterwards.
    function renderShell(host) {
        host.textContent = '';

        var header = document.createElement('div');
        header.className = 'protvTabHeader';

        var title = document.createElement('h2');
        title.className = 'protvTabTitle';

        var count = document.createElement('span');
        count.className = 'protvTabCount';

        var search = document.createElement('input');
        search.className = 'protvTabSearch';
        search.type = 'search';
        search.placeholder = 'Filter channels';
        search.value = state.filter;
        search.addEventListener('input', function () {
            if (state.view === 'channels') {
                state.filter = search.value;
                renderGrid();
                renderHeader();
                return;
            }

            if (state.view === 'all') {
                if (vodSearchTimer) {
                    window.clearTimeout(vodSearchTimer);
                }

                if (!search.value.trim()) {
                    loadAll('');
                    return;
                }

                vodSearchTimer = window.setTimeout(function () {
                    loadAll(search.value);
                }, 450);
                return;
            }

            state.vodFilter = search.value;
            if (vodSearchTimer) {
                window.clearTimeout(vodSearchTimer);
            }

            vodSearchTimer = window.setTimeout(function () {
                loadVod(1);
            }, 450);
        });
        search.addEventListener('keydown', function (event) {
            if (event.key !== 'Enter' || state.view === 'channels') {
                return;
            }

            event.preventDefault();
            if (vodSearchTimer) {
                window.clearTimeout(vodSearchTimer);
            }

            if (state.view === 'all') {
                loadAll(search.value);
            } else {
                loadVod(1);
            }
        });

        // Channels / Films / Series / All. The VOD views browse the PROTV catalogue; All runs one
        // search across live channels, films and series.
        var views = document.createElement('div');
        views.className = 'protvTabViews';
        [['channels', 'Channels'], ['movies', 'Films'], ['series', 'Series'], ['all', 'All']].forEach(function (entry) {
            var view = document.createElement('button');
            view.className = 'protvTabView';
            view.type = 'button';
            view.setAttribute('data-view', entry[0]);
            view.textContent = entry[1];
            view.addEventListener('click', function () {
                switchView(entry[0]);
            });
            views.appendChild(view);
        });

        var button = document.createElement('button');
        button.className = 'protvTabButton protvTabRefresh';
        button.type = 'button';
        button.textContent = 'Refresh';
        button.addEventListener('click', function () {
            if (state.view === 'channels') {
                load(true);
            } else if (state.view === 'all') {
                loadAll(state.allQuery);
            } else {
                loadVod(1);
            }
        });

        header.appendChild(title);
        header.appendChild(count);
        header.appendChild(views);
        header.appendChild(search);
        header.appendChild(button);

        var hint = document.createElement('p');
        hint.className = 'protvTabHint';
        hint.textContent = 'Pick a channel to play it here (the same Live TV channel the rest of '
            + 'Jellyfin plays). Use \u2139 on a channel to open its Jellyfin page instead.';

        var grid = document.createElement('div');
        grid.id = GRID_ID;
        grid.className = 'protvTabGrid';

        var more = document.createElement('button');
        more.id = MORE_ID;
        more.className = 'protvTabButton protvTabMore';
        more.type = 'button';
        more.style.display = 'none';
        more.addEventListener('click', function () {
            if (state.vodLoading) {
                return;
            }

            loadVod(state.vodPage + 1, true);
        });

        var message = document.createElement('div');
        message.id = MESSAGE_ID;
        message.className = 'protvTabMessage';

        host.appendChild(header);
        host.appendChild(hint);
        host.appendChild(message);
        host.appendChild(grid);
        host.appendChild(more);
    }

    function renderHeader() {
        var host = document.getElementById(ROOT_ID);
        if (!host) {
            return;
        }

        var title = host.querySelector('.protvTabTitle');
        var count = host.querySelector('.protvTabCount');
        var button = host.querySelector('.protvTabRefresh');
        var search = host.querySelector('.protvTabSearch');
        var hint = host.querySelector('.protvTabHint');
        if (title) {
            title.textContent = state.title;
        }

        var busy = state.view === 'channels'
            ? state.loading
            : state.view === 'all'
                ? state.allLoading
                : state.vodLoading;
        var views = host.querySelectorAll('.protvTabView');
        for (var index = 0; index < views.length; index++) {
            views[index].setAttribute(
                'data-active',
                views[index].getAttribute('data-view') === state.view ? '1' : '0');
        }

        if (button) {
            button.disabled = busy;
            button.textContent = busy
                ? (state.view === 'channels' ? 'Refreshing...' : state.view === 'all' ? 'Searching...' : 'Loading...')
                : 'Refresh';
        }

        if (search) {
            if (state.view === 'channels') {
                search.placeholder = 'Filter channels';
                search.style.display = state.channels.length ? '' : 'none';
            } else if (state.view === 'all') {
                search.placeholder = 'Search channels, films and series';
                search.style.display = '';
            } else {
                search.placeholder = state.view === 'series' ? 'Search series' : 'Search films';
                search.style.display = '';
            }
        }

        if (hint) {
            if (state.view === 'channels') {
                hint.textContent = 'Pick a channel to play it here (the same Live TV channel the rest of '
                    + 'Jellyfin plays). Use \u2139 on a channel to open its Jellyfin page instead.';
            } else if (state.view === 'all') {
                hint.textContent = 'Type to search every live channel, film and series in PROTV.';
            } else {
                hint.textContent = 'Films and episodes play here once they are in the PROTV VOD library (see the '
                    + 'PROTV dashboard page). Series open their season list.';
            }
        }

        if (!count) {
            return;
        }

        if (state.view === 'channels') {
            if (state.error || !state.channels.length) {
                count.textContent = '';
            } else {
                var visible = visibleChannels().length;
                count.textContent = visible === state.total
                    ? state.total + (state.total === 1 ? ' channel' : ' channels')
                    : visible + ' of ' + state.total + ' channels';
            }

            return;
        }

        if (state.view === 'all') {
            if (!state.allQuery.trim() || state.allLoading || state.allError) {
                count.textContent = '';
                return;
            }

            var filter = state.allQuery.trim().toLowerCase();
            var found = state.channels.filter(function (channel) {
                return String(channel.name || '').toLowerCase().indexOf(filter) >= 0
                    || String(channel.number || '') === filter;
            }).length;
            found += state.allMovies.length + state.allSeries.length;
            count.textContent = found + (found === 1 ? ' result' : ' results');
            return;
        }

        if (state.vodSeries || !state.vodItems.length) {
            count.textContent = '';
            return;
        }

        var noun = state.view === 'series' ? 'series' : 'films';
        count.textContent = state.vodItems.length === state.vodTotal
            ? state.vodTotal + ' ' + noun
            : state.vodItems.length + ' of ' + state.vodTotal + ' ' + noun;
    }

    function loadAll(query) {
        state.allQuery = String(query || '');
        state.allError = null;
        var request = (state.allRequest || 0) + 1;
        state.allRequest = request;

        if (!state.allQuery.trim()) {
            state.allLoading = false;
            state.allMovies = [];
            state.allSeries = [];
            renderHeader();
            renderGrid();
            return;
        }

        state.allLoading = true;
        renderHeader();
        renderGrid();

        var search = '?page=1&pageSize=12&search=' + encodeURIComponent(state.allQuery.trim());
        Promise.all([
            getJson(url('PROTV/Vod/Movies', search)),
            getJson(url('PROTV/Vod/Series', search))
        ]).then(function (results) {
            if (state.allRequest !== request) {
                return;
            }

            state.allLoading = false;
            state.allMovies = ((results[0] && results[0].items) || []).slice(0, 12);
            state.allSeries = ((results[1] && results[1].items) || []).slice(0, 12);
            renderHeader();
            renderGrid();
        }, function (error) {
            if (state.allRequest !== request) {
                return;
            }

            state.allLoading = false;
            state.allError = describeVod(error);
            renderHeader();
            renderGrid();
        });
    }

    function renderAllBody() {
        var grid = document.getElementById(GRID_ID);
        var message = document.getElementById(MESSAGE_ID);
        if (!grid || !message) {
            return;
        }

        grid.className = 'protvTabGrid';
        grid.textContent = '';

        if (!state.allQuery.trim()) {
            message.className = 'protvTabMessage';
            message.textContent = 'Search channels, films and series.';
            return;
        }

        if (state.allError) {
            setMessage(state.allError, 'bad');
            return;
        }

        if (state.allLoading) {
            message.className = 'protvTabMessage';
            message.textContent = 'Searching...';
            return;
        }

        var filter = state.allQuery.trim().toLowerCase();
        var channels = state.channels.filter(function (channel) {
            return String(channel.name || '').toLowerCase().indexOf(filter) >= 0
                || String(channel.number || '') === filter;
        }).slice(0, 24);

        if (!channels.length && !state.allMovies.length && !state.allSeries.length) {
            message.className = 'protvTabMessage';
            message.textContent = 'No results for "' + state.allQuery.trim() + '".';
            return;
        }

        renderMessage();
        if (channels.length) {
            var channelHeading = document.createElement('div');
            channelHeading.className = 'protvTabSectionTitle';
            channelHeading.textContent = 'Channels';
            grid.appendChild(channelHeading);
            channels.forEach(function (channel) {
                grid.appendChild(card(channel));
            });
        }

        if (state.allMovies.length || state.allSeries.length) {
            var vodHeading = document.createElement('div');
            vodHeading.className = 'protvTabSectionTitle';
            vodHeading.textContent = 'Films and series';
            grid.appendChild(vodHeading);
            state.allMovies.forEach(function (item) {
                grid.appendChild(vodCard(item));
            });
            state.allSeries.forEach(function (item) {
                grid.appendChild(vodCard(item));
            });
        }
    }

    function renderGrid() {
        var grid = document.getElementById(GRID_ID);
        var message = document.getElementById(MESSAGE_ID);
        if (!grid || !message) {
            return;
        }

        if (state.view === 'all') {
            renderAllBody();
            return;
        }

        if (state.view !== 'channels') {
            renderVodBody();
            return;
        }

        grid.className = 'protvTabGrid';
        grid.textContent = '';

        if (state.error && !state.channels.length) {
            setMessage(state.error, 'bad');
            return;
        }

        if (state.loading && !state.channels.length) {
            message.className = 'protvTabMessage';
            message.textContent = 'Loading channels...';
            return;
        }

        if (!state.channels.length) {
            message.className = 'protvTabMessage';
            message.textContent = 'No channels yet. Add the PROTV tuner to Live TV (or press Refresh) '
                + 'and the channels PROTV returns will be listed here.';
            return;
        }

        var visible = visibleChannels();
        if (!visible.length) {
            message.className = 'protvTabMessage';
            message.textContent = 'No channel matches "' + state.filter + '".';
            return;
        }

        var row = nowRow();
        if (row) {
            grid.appendChild(row);
        }

        var missing = 0;
        var sections = channelSections(visible);
        sections.forEach(function (section) {
            if (section.name) {
                var heading = document.createElement('div');
                heading.className = 'protvTabSectionTitle' + (section.group ? ' protvTabGroupName' : '');
                heading.textContent = section.name;
                grid.appendChild(heading);
            }

            section.channels.forEach(function (channel) {
                if (channel.inLiveTv === false) {
                    missing++;
                }

                grid.appendChild(card(channel));
            });
        });

        if (state.error) {
            // A refresh failed but the last channel list is still usable: keep it on screen with
            // a short banner instead of replacing the whole tab with an error.
            setMessage('PROTV is unavailable; showing the last channel list.', 'bad');
        } else if (missing) {
            setMessage(missing + (missing === 1 ? ' channel is' : ' channels are')
                + ' not in Jellyfin Live TV yet. Run the "Refresh Guide" task in the Jellyfin '
                + 'dashboard, then press Refresh.', 'bad');
        } else {
            renderMessage();
        }
    }

    function render() {
        var host = document.getElementById(ROOT_ID);
        if (!host) {
            // Custom Tabs may execute this before the tab container is in the DOM.
            if (waits < 50) {
                waits++;
                window.setTimeout(render, 100);
            }

            return;
        }

        ensureStyles();
        if (!host.querySelector('.' + 'protvTabHeader')) {
            renderShell(host);
        }

        renderHeader();
        renderGrid();
        // Swaps the strings for Arabic when it applies and keeps doing so after every re-render.
        watchLanguage(host);
        if (!state.requested) {
            load(false);
        }
    }

    window.ProTvTab = {
        render: render,
        refresh: function () {
            load(true);
        }
    };

    render();
})();
