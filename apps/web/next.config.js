import createNextIntlPlugin from "next-intl/plugin";

/** @type {import('next').NextConfig} */
const nextConfig = {
  transpilePackages: ["@repo/components"],
  images: {
    remotePatterns: [
      // Placeholder product imagery until the API serves real assets.
      // picsum.photos redirects to its CDN, so both hosts must be allowed.
      { protocol: "https", hostname: "picsum.photos", pathname: "/seed/**" },
      { protocol: "https", hostname: "fastly.picsum.photos" },
    ],
  },
};

const withNextIntl = createNextIntlPlugin();

export default withNextIntl(nextConfig);
