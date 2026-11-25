(() => {
            const ua = navigator.userAgent || '';
            const ios =
                /iphone|ipad|ipod/i.test(ua) ||
                (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
            const android = /android/i.test(ua);
            const standalone =
                window.matchMedia('(display-mode: standalone)').matches ||
                navigator.standalone === true;

            document.documentElement.classList.toggle('ios-device', ios);
            document.documentElement.classList.toggle('android-device', android);
            document.documentElement.classList.toggle('standalone-app', standalone);
        })();
