(() => {
  'use strict';

  const appUrl = 'https://app.svault.ir';
  document.querySelectorAll('[data-app-link]').forEach((link) => link.href = appUrl);

  const header = document.querySelector('.site-header');
  const menuButton = document.querySelector('.menu-button');
  const mobileMenu = document.querySelector('.mobile-menu');
  const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  function setMenu(open) {
    if (menuButton) menuButton.setAttribute('aria-expanded', String(open));
    if (mobileMenu) mobileMenu.hidden = !open;
    document.body.classList.toggle('menu-open', open);
  }

  menuButton?.addEventListener('click', () => setMenu(menuButton.getAttribute('aria-expanded') !== 'true'));
  mobileMenu?.querySelectorAll('a').forEach((link) => link.addEventListener('click', () => setMenu(false)));
  window.addEventListener('resize', () => { if (window.innerWidth > 1100) setMenu(false); }, { passive: true });

  const updateHeader = () => header?.classList.toggle('scrolled', window.scrollY > 16);
  updateHeader();
  window.addEventListener('scroll', updateHeader, { passive: true });

  document.querySelectorAll('.reveal').forEach((el) => el.style.setProperty('--delay', `${el.dataset.delay || 0}ms`));

  const revealObserver = new IntersectionObserver((entries, observer) => {
    entries.forEach((entry) => {
      if (!entry.isIntersecting) return;
      entry.target.classList.add('is-visible');
      if (entry.target.querySelectorAll('[data-typewriter]').length) {
        entry.target.querySelectorAll('[data-typewriter]').forEach(startTypewriter);
      }
      observer.unobserve(entry.target);
    });
  }, { threshold: .14, rootMargin: '0px 0px -6% 0px' });
  document.querySelectorAll('.reveal').forEach((el) => revealObserver.observe(el));

  function startTypewriter(el) {
    if (!el || el.dataset.typed === 'true') return;
    const text = el.getAttribute('data-typewriter') || el.textContent.trim();
    el.dataset.typed = 'true';
    if (reducedMotion) {
      el.textContent = text;
      el.classList.add('typed');
      return;
    }
    el.textContent = '';
    el.classList.add('is-typing');
    let i = 0;
    const step = () => {
      el.textContent = text.slice(0, i);
      i += 1;
      if (i <= text.length) {
        setTimeout(step, text.length > 24 ? 34 : 52);
      } else {
        el.classList.remove('is-typing');
        el.classList.add('typed');
      }
    };
    step();
  }

  // Type the hero boot line early if visible on first paint.
  document.querySelectorAll('.hero [data-typewriter]').forEach((el) => {
    const rect = el.getBoundingClientRect();
    if (rect.top < window.innerHeight * 0.92) startTypewriter(el);
  });

  document.querySelectorAll('.accordion-item button').forEach((button) => {
    button.addEventListener('click', () => {
      const item = button.closest('.accordion-item');
      const willOpen = !item.classList.contains('is-open');
      document.querySelectorAll('.accordion-item').forEach((other) => {
        other.classList.remove('is-open');
        other.querySelector('button')?.setAttribute('aria-expanded', 'false');
      });
      if (willOpen) {
        item.classList.add('is-open');
        button.setAttribute('aria-expanded', 'true');
      }
    });
  });

  if (!reducedMotion && window.matchMedia('(pointer:fine)').matches) {
    const glow = document.querySelector('.cursor-glow');
    window.addEventListener('mousemove', (event) => {
      if (!glow) return;
      glow.style.opacity = '1';
      glow.style.transform = `translate(${event.clientX - 210}px, ${event.clientY - 210}px)`;
    }, { passive: true });

    document.querySelectorAll('[data-tilt]').forEach((element) => {
      const strength = Number(element.dataset.strength || 5);
      element.addEventListener('mousemove', (event) => {
        const rect = element.getBoundingClientRect();
        const x = (event.clientX - rect.left) / rect.width - .5;
        const y = (event.clientY - rect.top) / rect.height - .5;
        element.style.transform = `perspective(1000px) rotateY(${x * strength}deg) rotateX(${-y * strength}deg)`;
      });
      element.addEventListener('mouseleave', () => element.style.transform = '');
    });

    document.querySelectorAll('.magnetic').forEach((button) => {
      button.addEventListener('mousemove', (event) => {
        const rect = button.getBoundingClientRect();
        button.style.transform = `translate(${(event.clientX - rect.left - rect.width / 2) * .1}px, ${(event.clientY - rect.top - rect.height / 2) * .1}px)`;
      });
      button.addEventListener('mouseleave', () => button.style.transform = '');
    });
  }

  let audioEnabled = !reducedMotion;
  let audioContext;
  function getCtx() {
    if (!audioEnabled) return null;
    if (!audioContext) audioContext = new (window.AudioContext || window.webkitAudioContext)();
    if (audioContext.state === 'suspended') audioContext.resume();
    return audioContext;
  }
  function beep(freq = 640, duration = 0.035, type = 'square', gainValue = 0.018) {
    const ctx = getCtx();
    if (!ctx) return;
    const osc = ctx.createOscillator();
    const gain = ctx.createGain();
    osc.type = type;
    osc.frequency.value = freq;
    gain.gain.value = 0.0001;
    osc.connect(gain);
    gain.connect(ctx.destination);
    const now = ctx.currentTime;
    gain.gain.exponentialRampToValueAtTime(gainValue, now + 0.005);
    gain.gain.exponentialRampToValueAtTime(0.0001, now + duration);
    osc.start(now);
    osc.stop(now + duration + 0.01);
  }

  const audioTargets = document.querySelectorAll('.button, .lang-switch, .menu-button, .accordion-item button, .desktop-nav a, .mobile-menu a');
  audioTargets.forEach((el) => {
    el.addEventListener('click', () => beep(720, 0.045, 'square', 0.015));
    if (window.matchMedia('(pointer:fine)').matches) {
      el.addEventListener('mouseenter', () => beep(560, 0.025, 'sine', 0.007));
    }
  });

  function initStarfield() {
    const canvas = document.getElementById('starfield');
    if (!canvas) return;
    const ctx = canvas.getContext('2d');
    let width = 0, height = 0, dpr = 1, stars = [];

    function resize() {
      dpr = Math.min(window.devicePixelRatio || 1, 2);
      width = canvas.width = Math.floor(window.innerWidth * dpr);
      height = canvas.height = Math.floor(window.innerHeight * dpr);
      canvas.style.width = `${window.innerWidth}px`;
      canvas.style.height = `${window.innerHeight}px`;
      stars = Array.from({ length: Math.max(44, Math.floor(window.innerWidth / 20)) }, () => ({
        x: Math.random() * width,
        y: Math.random() * height,
        z: Math.random() * 1 + .3,
        size: Math.random() * 1.2 + .25,
        speed: Math.random() * .10 + .025
      }));
    }

    function render() {
      ctx.clearRect(0, 0, width, height);
      for (const star of stars) {
        star.y += star.speed * dpr;
        if (star.y > height) { star.y = -10; star.x = Math.random() * width; }
        ctx.beginPath();
        ctx.fillStyle = `rgba(130,255,196,${0.14 + star.z * 0.32})`;
        ctx.arc(star.x, star.y, star.size * dpr, 0, Math.PI * 2);
        ctx.fill();
      }
      requestAnimationFrame(render);
    }
    resize();
    render();
    window.addEventListener('resize', resize, { passive: true });
  }

  if (!reducedMotion) initStarfield();
  document.getElementById('year')?.replaceChildren(document.createTextNode(String(new Date().getFullYear())));
})();
